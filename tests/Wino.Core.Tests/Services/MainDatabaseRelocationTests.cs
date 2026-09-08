using FluentAssertions;
using Moq;
using SQLite;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Domain.Models.Migration;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class MainDatabaseRelocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wino-relocation-" + Guid.NewGuid().ToString("N"));

    private ApplicationConfiguration Configuration(bool stable = true, string identity = "stable")
    {
        var configuration = new ApplicationConfiguration
        {
            ApplicationDataFolderPath = Path.Combine(_root, identity, "LocalState"),
            PublisherSharedFolderPath = Path.Combine(_root, "publisher"),
            AllowLegacyDataMigration = stable
        };
        Directory.CreateDirectory(configuration.ApplicationDataFolderPath);
        Directory.CreateDirectory(configuration.PublisherSharedFolderPath);
        return configuration;
    }

    [Fact]
    public async Task SnapshotIncludesCommittedWalAndNeverChangesSource()
    {
        var configuration = Configuration();
        var source = Path.Combine(configuration.PublisherSharedFolderPath, DatabaseService.LegacyDatabaseName);
        using var writer = new SQLiteConnection(source);
        writer.ExecuteScalar<string>("PRAGMA journal_mode=WAL");
        writer.ExecuteScalar<int>("PRAGMA wal_autocheckpoint=0");
        writer.Execute("CREATE TABLE Evidence (Value TEXT)");
        writer.Execute("INSERT INTO Evidence VALUES ('committed in WAL')");
        File.Exists(source + "-wal").Should().BeTrue();
        var bytes = await ReadSharedAsync(source);

        await new MainDatabaseRelocationService(configuration, new DatabaseSchemaService(configuration)).PrepareAsync();

        using var copy = new SQLiteConnection(MainDatabasePaths.GetPath(configuration, DatabaseService.LegacyDatabaseName));
        copy.ExecuteScalar<string>("SELECT Value FROM Evidence").Should().Be("committed in WAL");
        (await ReadSharedAsync(source)).Should().Equal(bytes);
        writer.ExecuteScalar<int>("SELECT COUNT(*) FROM Evidence").Should().Be(1);
    }

    [Fact]
    public async Task RetryKeepsSuccessfulLocalSnapshot()
    {
        var configuration = Configuration();
        var source = Path.Combine(configuration.PublisherSharedFolderPath, DatabaseService.LegacyDatabaseName);
        using var writer = new SQLiteConnection(source);
        writer.Execute("CREATE TABLE Evidence (Value INTEGER)");
        writer.Execute("INSERT INTO Evidence VALUES (1)");
        var relocation = new MainDatabaseRelocationService(configuration, new DatabaseSchemaService(configuration));
        await relocation.PrepareAsync();
        writer.Execute("INSERT INTO Evidence VALUES (2)");

        await relocation.PrepareAsync();

        using var copy = new SQLiteConnection(MainDatabasePaths.GetPath(configuration, DatabaseService.LegacyDatabaseName));
        copy.ExecuteScalar<int>("SELECT COUNT(*) FROM Evidence").Should().Be(1);
    }

    [Fact]
    public async Task BetaDoesNotInspectPublisherOrLegacyTokens()
    {
        var configuration = Configuration(stable: false, identity: "beta");
        await File.WriteAllTextAsync(Path.Combine(configuration.PublisherSharedFolderPath, DatabaseService.CurrentDatabaseName), "corrupt shared database");
        var token = Path.Combine(configuration.PublisherSharedFolderPath, AuthenticationTokenStorePaths.OutlookTokenCacheFileName);
        await File.WriteAllTextAsync(token, "shared credentials");

        await new MainDatabaseRelocationService(configuration, new DatabaseSchemaService(configuration)).PrepareAsync();
        var tokens = new AuthenticationTokenMigrationService(configuration);
        await tokens.PrepareAsync([]);
        await tokens.FinalizeAsync([]);

        Directory.GetFiles(configuration.ApplicationDataFolderPath).Should().BeEmpty();
        (await File.ReadAllTextAsync(token)).Should().Be("shared credentials");
        var database = new DatabaseService(configuration);
        await database.InitializeAsync();
        await database.Connection.CloseAsync();
        File.Exists(MainDatabasePaths.GetPath(configuration, DatabaseService.CurrentDatabaseName)).Should().BeTrue();
    }

    [Fact]
    public async Task ExistingLocalDatabaseWinsOverCorruptPublisher()
    {
        var configuration = Configuration();
        var database = new DatabaseService(configuration);
        await database.InitializeAsync();
        await database.Connection.CloseAsync();
        await File.WriteAllTextAsync(Path.Combine(configuration.PublisherSharedFolderPath, DatabaseService.CurrentDatabaseName), "corrupt");
        await new MainDatabaseRelocationService(configuration, new DatabaseSchemaService(configuration)).PrepareAsync();
        (await new DatabaseSchemaService(configuration).ValidateAsync(MainDatabasePaths.GetPath(configuration, DatabaseService.CurrentDatabaseName), requireCompletedMigration: true)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task CorruptPublisherBlocksStartupAndCanBeRetried()
    {
        var configuration = Configuration();
        var source = Path.Combine(configuration.PublisherSharedFolderPath, DatabaseService.LegacyDatabaseName);
        await File.WriteAllTextAsync(source, "corrupt");
        var coordinator = new DatabaseMigrationCoordinator(configuration, new DatabaseSchemaService(configuration), Mock.Of<IAccountProfilePictureFileService>());
        (await coordinator.InspectAsync()).Status.Should().Be(MigrationStatus.Failed);
        (await coordinator.RunAsync([])).Status.Should().Be(MigrationStatus.Failed);
        File.Exists(MainDatabasePaths.GetPath(configuration, DatabaseService.CurrentDatabaseName)).Should().BeFalse();
        (await File.ReadAllTextAsync(source)).Should().Be("corrupt");

        File.Delete(source);
        using (var repaired = new SQLiteConnection(source))
            repaired.CreateTable<Wino.Core.Domain.Entities.Shared.MailAccount>();

        (await coordinator.InspectAsync()).Status.Should().Be(MigrationStatus.Required);
        File.Exists(MainDatabasePaths.GetPath(configuration, DatabaseService.LegacyDatabaseName)).Should().BeTrue();
    }

    [Fact]
    public async Task CompletedDatabaseIsCopiedIndependentlyToBothStableIdentities()
    {
        var first = Configuration(identity: "store");
        var publisherConfiguration = new ApplicationConfiguration { ApplicationDataFolderPath = first.PublisherSharedFolderPath };
        var database = new DatabaseService(publisherConfiguration);
        await database.InitializeAsync();
        await database.Connection.ExecuteAsync("CREATE TABLE Evidence (Value INTEGER)");
        await database.Connection.ExecuteAsync("INSERT INTO Evidence VALUES (1)");
        await database.Connection.CloseAsync();
        var second = Configuration(identity: "sideload");

        foreach (var configuration in new[] { first, second })
            await new MainDatabaseRelocationService(configuration, new DatabaseSchemaService(configuration)).PrepareAsync();

        using var store = new SQLiteConnection(MainDatabasePaths.GetPath(first, DatabaseService.CurrentDatabaseName));
        store.Execute("DELETE FROM Evidence");
        using var sideload = new SQLiteConnection(MainDatabasePaths.GetPath(second, DatabaseService.CurrentDatabaseName));
        sideload.ExecuteScalar<int>("SELECT COUNT(*) FROM Evidence").Should().Be(1);
        using var publisher = new SQLiteConnection(MainDatabasePaths.GetPath(publisherConfiguration, DatabaseService.CurrentDatabaseName));
        publisher.ExecuteScalar<int>("SELECT COUNT(*) FROM Evidence").Should().Be(1);
    }

    [Fact]
    public async Task InterruptedSnapshotIsReplacedBeforeMigration()
    {
        var configuration = Configuration();
        var source = Path.Combine(configuration.PublisherSharedFolderPath, DatabaseService.LegacyDatabaseName);
        using (var writer = new SQLiteConnection(source))
            writer.Execute("CREATE TABLE Evidence (Value INTEGER)");
        var destination = MainDatabasePaths.GetPath(configuration, DatabaseService.LegacyDatabaseName);
        await File.WriteAllTextAsync(destination + ".copying", "partial snapshot");
        await new MainDatabaseRelocationService(configuration, new DatabaseSchemaService(configuration)).PrepareAsync();
        using var copy = new SQLiteConnection(destination);
        copy.ExecuteScalar<string>("PRAGMA integrity_check").Should().Be("ok");
        File.Exists(destination + ".copying").Should().BeFalse();
    }

    [Fact]
    public async Task InsufficientSpaceNeverCreatesMigrationInput()
    {
        var configuration = Configuration();
        var source = Path.Combine(configuration.PublisherSharedFolderPath, DatabaseService.LegacyDatabaseName);
        using (var writer = new SQLiteConnection(source))
            writer.Execute("CREATE TABLE Evidence (Value INTEGER)");

        var relocation = new MainDatabaseRelocationService(configuration, new DatabaseSchemaService(configuration), _ => 0);
        var operation = () => relocation.PrepareAsync();
        await operation.Should().ThrowAsync<IOException>().WithMessage("*not enough space*");
        Directory.GetFiles(configuration.ApplicationDataFolderPath).Should().BeEmpty();
        File.Exists(source).Should().BeTrue();
    }

    [Fact]
    public async Task CancellationDoesNotPromoteOrReplaceLocalData()
    {
        var configuration = Configuration();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var relocation = new MainDatabaseRelocationService(configuration, new DatabaseSchemaService(configuration));
        var operation = () => relocation.PrepareAsync(cancellation.Token);
        await operation.Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(configuration.ApplicationDataFolderPath).Should().BeEmpty();
    }

    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
