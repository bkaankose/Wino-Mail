using System.Security.Cryptography;
using FluentAssertions;
using Moq;
using SQLite;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Migration;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class Database210MigrationCoordinatorTests
{
    [Fact]
    public async Task RunAsync_CopiesLegacyDataWithoutChangingSourceOrCopyingLegacyContacts()
    {
        var directory = CreateTemporaryDirectory();
        var sourcePath = Path.Combine(directory, DatabaseService.LegacyDatabaseName);
        var accountId = Guid.NewGuid();
        var mailId = Guid.NewGuid();

        try
        {
            var source = new SQLiteAsyncConnection(sourcePath);
            await source.CreateTableAsync<MailAccount>();
            await source.CreateTableAsync<MailCopy>();
            await source.InsertAsync(new MailAccount
            {
                Id = accountId,
                Name = "Legacy Gmail",
                Address = "legacy@example.com",
                ProviderType = MailProviderType.Gmail,
                IsMailAccessGranted = true,
                IsCalendarAccessGranted = true
            });
            await source.InsertAsync(new MailCopy
            {
                UniqueId = mailId,
                Id = "message-1",
                FileId = Guid.NewGuid(),
                Subject = "Preserve me",
                IsDraft = true
            });
            await source.ExecuteAsync("CREATE TABLE AccountContact (Address TEXT PRIMARY KEY, Name TEXT);");
            await source.ExecuteAsync("INSERT INTO AccountContact (Address, Name) VALUES ('person@example.com', 'Person');");
            await source.ExecuteAsync("CREATE TABLE ContactGroup (Id TEXT PRIMARY KEY, Name TEXT);");
            await source.ExecuteAsync("CREATE TABLE ContactGroupMember (Id INTEGER PRIMARY KEY, GroupId TEXT, MemberAddress TEXT);");
            await source.CloseAsync();

            var sourceHash = ComputeHash(sourcePath);
            var coordinator = CreateCoordinator(directory);
            var plan = await coordinator.InspectAsync();

            plan.Status.Should().Be(MigrationStatus.Required);
            plan.Accounts.Should().ContainSingle();

            var result = await coordinator.RunAsync(plan.Accounts);

            result.Status.Should().Be(MigrationStatus.Completed, $"{result.FailedStep}: {result.ErrorMessage}");
            File.Exists(Path.Combine(directory, DatabaseService.CurrentDatabaseName)).Should().BeTrue();
            ComputeHash(sourcePath).Should().Equal(sourceHash);

            var destination = new SQLiteAsyncConnection(Path.Combine(directory, DatabaseService.CurrentDatabaseName));
            (await destination.FindAsync<MailAccount>(accountId)).Should().NotBeNull();
            (await destination.FindAsync<MailCopy>(mailId)).Subject.Should().Be("Preserve me");
            (await destination.GetTableInfoAsync("AccountContact")).Should().BeEmpty();
            (await destination.GetTableInfoAsync("ContactGroup")).Should().BeEmpty();
            (await destination.Table<AccountContact>().CountAsync()).Should().Be(0);

            var migratedAccount = await destination.FindAsync<MailAccount>(accountId);
            migratedAccount.IsCalendarAccessEnabled.Should().BeTrue();
            migratedAccount.CalendarIntegrationSource.Should().Be(AccountIntegrationSource.Provider);
            migratedAccount.IsContactAccessEnabled.Should().BeTrue();
            migratedAccount.IsContactAccessGranted.Should().BeFalse();
            migratedAccount.IsContactReauthorizationRequired.Should().BeTrue();
            migratedAccount.AttentionReason.Should().Be(AccountAttentionReason.InvalidCredentials);
            await destination.CloseAsync();

            var normalDatabase = new DatabaseService(CreateConfiguration(directory));
            await normalDatabase.InitializeAsync();
            (await normalDatabase.Connection.Table<MailAccount>().CountAsync()).Should().Be(1);
            await normalDatabase.Connection.CloseAsync();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StartFreshAsync_CreatesValidEmptyDatabaseAndKeepsLegacyDatabase()
    {
        var directory = CreateTemporaryDirectory();
        var sourcePath = Path.Combine(directory, DatabaseService.LegacyDatabaseName);

        try
        {
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
            var sourceHash = ComputeHash(sourcePath);
            var coordinator = CreateCoordinator(directory);

            (await coordinator.InspectAsync()).Status.Should().Be(MigrationStatus.Required);
            var failedMigration = await coordinator.RunAsync([]);
            failedMigration.Status.Should().Be(MigrationStatus.Failed);
            failedMigration.FailedStep.Should().Be(MigrationStepKind.CheckExistingData);
            ComputeHash(sourcePath).Should().Equal(sourceHash);

            var result = await coordinator.StartFreshAsync();

            result.Status.Should().Be(MigrationStatus.Skipped);
            ComputeHash(sourcePath).Should().Equal(sourceHash);

            var schema = new DatabaseSchemaService(CreateConfiguration(directory));
            var validation = await schema.ValidateAsync(Path.Combine(directory, DatabaseService.CurrentDatabaseName));
            validation.IsValid.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ResumesAfterFeatureFailureAndKeepsEveryStepVisibleForOneSecond()
    {
        var directory = CreateTemporaryDirectory();
        var sourcePath = Path.Combine(directory, DatabaseService.LegacyDatabaseName);
        var accountId = Guid.NewGuid();

        try
        {
            var source = new SQLiteAsyncConnection(sourcePath);
            await source.CreateTableAsync<MailAccount>();
            await source.InsertAsync(new MailAccount
            {
                Id = accountId,
                Name = "Ünicode Outlook",
                Address = "unicode@example.com",
                ProviderType = MailProviderType.Outlook,
                Base64ProfilePictureData = Convert.ToBase64String([1, 2, 3]),
                IsMailAccessGranted = true
            });
            await source.CloseAsync();

            var failingPictures = new Mock<IAccountProfilePictureFileService>();
            failingPictures.Setup(service => service.SaveProfilePictureAsync(
                    It.IsAny<byte[]>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("simulated profile storage failure"));
            var firstClock = new FakeMigrationClock();
            var firstCoordinator = CreateCoordinator(directory, failingPictures.Object, firstClock);
            var plan = await firstCoordinator.InspectAsync();

            var firstResult = await firstCoordinator.RunAsync(plan.Accounts);

            firstResult.Status.Should().Be(MigrationStatus.Failed);
            firstResult.FailedStep.Should().Be(MigrationStepKind.ConfigureFeatures);
            File.Exists(Path.Combine(directory, "Wino210.db.migrating")).Should().BeTrue();
            firstClock.Delays.Should().OnlyContain(delay => delay == TimeSpan.FromSeconds(1));

            var retryClock = new FakeMigrationClock();
            var retryCoordinator = CreateCoordinator(directory, clock: retryClock);
            var retryPlan = await retryCoordinator.InspectAsync();
            retryPlan.CanResume.Should().BeTrue();

            var retryResult = await retryCoordinator.RunAsync(retryPlan.Accounts);

            retryResult.Status.Should().Be(MigrationStatus.Completed, retryResult.ErrorMessage);
            var destination = new SQLiteAsyncConnection(Path.Combine(directory, DatabaseService.CurrentDatabaseName));
            var account = await destination.FindAsync<MailAccount>(accountId);
            account.Name.Should().Be("Ünicode Outlook");
            account.ProfilePictureFileId.Should().NotBeNull();
            account.Base64ProfilePictureData.Should().BeEmpty();
            var metadata = await destination.FindAsync<DatabaseMigrationCoordinator.MigrationMetadataRow>(1);
            metadata.Status.Should().Be(MigrationStatus.Completed);
            metadata.LastCompletedStep.Should().Be((int)MigrationStepKind.Completed);
            metadata.DeferredAccountIds.Should().Contain(accountId.ToString("N"));
            await destination.CloseAsync();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InspectAsync_Invalid210WithoutLegacyDatabase_RequiresFreshStart()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, DatabaseService.CurrentDatabaseName), [9, 8, 7]);
            var coordinator = CreateCoordinator(directory);

            var plan = await coordinator.InspectAsync();

            plan.Status.Should().Be(MigrationStatus.Required);
            plan.Accounts.Should().BeEmpty();
            (await coordinator.StartFreshAsync()).Status.Should().Be(MigrationStatus.Skipped);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static DatabaseMigrationCoordinator CreateCoordinator(
        string directory,
        IAccountProfilePictureFileService pictureService = null,
        IMigrationClock clock = null)
    {
        var configuration = CreateConfiguration(directory);
        if (pictureService == null)
        {
            var pictureServiceMock = new Mock<IAccountProfilePictureFileService>();
            pictureServiceMock.Setup(service => service.SaveProfilePictureAsync(
                    It.IsAny<byte[]>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.NewGuid());
            pictureService = pictureServiceMock.Object;
        }

        return new DatabaseMigrationCoordinator(
            configuration,
            new DatabaseSchemaService(configuration),
            pictureService,
            clock ?? new FakeMigrationClock());
    }

    private static IApplicationConfiguration CreateConfiguration(string directory)
    {
        var configuration = new Mock<IApplicationConfiguration>();
        configuration.SetupProperty(item => item.PublisherSharedFolderPath, directory);
        configuration.SetupProperty(item => item.ApplicationDataFolderPath, directory);
        return configuration.Object;
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wino-210-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static byte[] ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private sealed class FakeMigrationClock : IMigrationClock
    {
        public DateTime UtcNow { get; private set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public List<TimeSpan> Delays { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            UtcNow += delay;
            return Task.CompletedTask;
        }
    }
}
