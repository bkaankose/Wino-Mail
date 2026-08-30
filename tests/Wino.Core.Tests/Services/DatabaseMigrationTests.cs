using FluentAssertions;
using Moq;
using SQLite;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public async Task InitializeAsync_AddsPop3IdentityAndDeletionPersistence()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wino-pop3-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, DatabaseService.CurrentDatabaseName);
        DatabaseService databaseService = null;

        try
        {
            var legacyConnection = new SQLiteAsyncConnection(databasePath);
            await legacyConnection.CreateTableAsync<MailCopy>();
            await legacyConnection.ExecuteAsync($"ALTER TABLE {nameof(MailCopy)} DROP COLUMN {nameof(MailCopy.Pop3Uidl)}");
            await MarkCompleted210Async(legacyConnection);
            await legacyConnection.CloseAsync();

            var configuration = new Mock<IApplicationConfiguration>();
            configuration.SetupProperty(item => item.PublisherSharedFolderPath, directory);
            databaseService = new DatabaseService(configuration.Object);
            await databaseService.InitializeAsync();

            (await databaseService.Connection.GetTableInfoAsync(nameof(MailCopy)))
                .Should().Contain(column => column.Name == nameof(MailCopy.Pop3Uidl));
            (await databaseService.Connection.GetTableInfoAsync(nameof(Pop3PendingServerDeletion)))
                .Should().NotBeEmpty();
            (await databaseService.Connection.GetTableInfoAsync(nameof(Pop3RemoteMessageState)))
                .Should().NotBeEmpty();
        }
        finally
        {
            if (databaseService?.Connection != null)
                await databaseService.Connection.CloseAsync();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_NewCardDavCreationCapability_ForcesOneTimeRediscovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wino-carddav-capability-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, DatabaseService.CurrentDatabaseName);
        DatabaseService databaseService = null;
        var accountId = Guid.NewGuid();

        try
        {
            var existing = new SQLiteAsyncConnection(databasePath);
            await existing.CreateTableAsync<CardDavAccountState>();
            await existing.ExecuteAsync(
                $"ALTER TABLE {nameof(CardDavAccountState)} DROP COLUMN {nameof(CardDavAccountState.SupportsAddressBookCreation)}");
            await existing.ExecuteAsync(
                $"INSERT INTO {nameof(CardDavAccountState)} ({nameof(CardDavAccountState.AccountId)}, {nameof(CardDavAccountState.RequiresRediscovery)}) VALUES (?, 0)",
                accountId);
            await MarkCompleted210Async(existing);
            await existing.CloseAsync();

            var configuration = new Mock<IApplicationConfiguration>();
            configuration.SetupProperty(item => item.PublisherSharedFolderPath, directory);
            databaseService = new DatabaseService(configuration.Object);

            await databaseService.InitializeAsync();

            var state = await databaseService.Connection.FindAsync<CardDavAccountState>(accountId);
            state.RequiresRediscovery.Should().BeTrue();
            (await databaseService.Connection.GetTableInfoAsync(nameof(CardDavAccountState)))
                .Should().Contain(column => column.Name == nameof(CardDavAccountState.SupportsAddressBookCreation));
        }
        finally
        {
            if (databaseService?.Connection != null) await databaseService.Connection.CloseAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_DoesNotRunLegacyContactMigrationInsideCompleted210Database()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wino-contact-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, DatabaseService.CurrentDatabaseName);
        DatabaseService databaseService = null;
        var accountId = Guid.NewGuid();

        try
        {
            var legacy = new SQLiteAsyncConnection(databasePath);
            await legacy.CreateTableAsync<MailAccount>();
            await legacy.ExecuteAsync("ALTER TABLE MailAccount DROP COLUMN IsContactAccessGranted");
            await legacy.ExecuteAsync("ALTER TABLE MailAccount DROP COLUMN IsContactReauthorizationRequired");
            await legacy.ExecuteAsync("INSERT INTO MailAccount (Id, Name, ProviderType) VALUES (?, ?, ?)", accountId, "Existing", MailProviderType.Gmail);
            await legacy.ExecuteAsync("CREATE TABLE AccountContact (Address TEXT PRIMARY KEY, Name TEXT, ContactPictureFileId TEXT)");
            await legacy.ExecuteAsync("INSERT INTO AccountContact (Address, Name) VALUES ('old@example.com', 'Old')");
            await legacy.ExecuteAsync("CREATE TABLE ContactGroup (Id TEXT PRIMARY KEY, Name TEXT)");
            await legacy.ExecuteAsync("CREATE TABLE ContactGroupMember (GroupId TEXT, MemberAddress TEXT)");
            await MarkCompleted210Async(legacy);
            await legacy.CloseAsync();

            var configuration = new Mock<IApplicationConfiguration>();
            configuration.SetupProperty(x => x.PublisherSharedFolderPath, directory);
            databaseService = new DatabaseService(configuration.Object);
            await databaseService.InitializeAsync();
            await databaseService.InitializeAsync();

            (await databaseService.Connection.GetTableInfoAsync("AccountContact"))
                .Should().Contain(column => column.Name == "Address");
            (await databaseService.Connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM AccountContact WHERE Address = 'old@example.com';")).Should().Be(1);
            (await databaseService.Connection.GetTableInfoAsync("ContactGroup")).Should().NotBeEmpty();
        }
        finally
        {
            if (databaseService?.Connection != null) await databaseService.Connection.CloseAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_AddsAccountProfilePictureColumns()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wino-profile-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, DatabaseService.CurrentDatabaseName);
        DatabaseService databaseService = null;

        try
        {
            var legacyConnection = new SQLiteAsyncConnection(databasePath);
            await legacyConnection.CreateTableAsync<MailAccount>();
            await legacyConnection.ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} DROP COLUMN {nameof(MailAccount.ProfilePictureFileId)}");
            await legacyConnection.ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} DROP COLUMN {nameof(MailAccount.IsProfilePictureBackfillComplete)}");
            await MarkCompleted210Async(legacyConnection);
            await legacyConnection.CloseAsync();

            var configuration = new Mock<IApplicationConfiguration>();
            configuration.SetupProperty(x => x.PublisherSharedFolderPath, directory);
            databaseService = new DatabaseService(configuration.Object);

            await databaseService.InitializeAsync();

            var columns = await databaseService.Connection.GetTableInfoAsync(nameof(MailAccount));
            columns.Should().Contain(column => column.Name == nameof(MailAccount.ProfilePictureFileId));
            columns.Should().Contain(column => column.Name == nameof(MailAccount.IsProfilePictureBackfillComplete));
        }
        finally
        {
            if (databaseService?.Connection != null)
                await databaseService.Connection.CloseAsync();

            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_AddsLegacyConnectionPolicyToExistingImapRows()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wino-policy-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, DatabaseService.CurrentDatabaseName);
        DatabaseService databaseService = null;
        var serverInformationId = Guid.NewGuid();

        try
        {
            var legacyConnection = new SQLiteAsyncConnection(databasePath);
            await legacyConnection.CreateTableAsync<CustomServerInformation>();
            await legacyConnection.ExecuteAsync(
                $"ALTER TABLE {nameof(CustomServerInformation)} DROP COLUMN {nameof(CustomServerInformation.ConnectionPolicyVersion)}");
            await legacyConnection.ExecuteAsync(
                $"INSERT INTO {nameof(CustomServerInformation)} ({nameof(CustomServerInformation.Id)}, {nameof(CustomServerInformation.AccountId)}) VALUES (?, ?)",
                serverInformationId, Guid.NewGuid());
            await MarkCompleted210Async(legacyConnection);
            await legacyConnection.CloseAsync();

            var configuration = new Mock<IApplicationConfiguration>();
            configuration.SetupProperty(x => x.PublisherSharedFolderPath, directory);
            databaseService = new DatabaseService(configuration.Object);

            await databaseService.InitializeAsync();

            var migrated = await databaseService.Connection.FindAsync<CustomServerInformation>(serverInformationId);
            migrated.ConnectionPolicyVersion.Should().Be(ImapConnectionPolicyVersion.Legacy);
        }
        finally
        {
            if (databaseService?.Connection != null)
                await databaseService.Connection.CloseAsync();

            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_BackfillsDraftState_UsingEscapedLocalPrefix()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wino-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, DatabaseService.CurrentDatabaseName);
        DatabaseService databaseService = null;

        try
        {
            var legacyConnection = new SQLiteAsyncConnection(databasePath);
            await legacyConnection.CreateTableAsync<MailCopy>();

            var localDraft = new MailCopy
            {
                UniqueId = Guid.NewGuid(),
                Id = "local",
                IsDraft = true,
                DraftId = "localDraft_123"
            };
            var underscoreNearMiss = new MailCopy
            {
                UniqueId = Guid.NewGuid(),
                Id = "mapped",
                IsDraft = true,
                DraftId = "localDraftX123"
            };

            await legacyConnection.InsertAllAsync(new[] { localDraft, underscoreNearMiss });
            await legacyConnection.ExecuteAsync("ALTER TABLE MailCopy DROP COLUMN DraftSyncState");
            await legacyConnection.ExecuteAsync("ALTER TABLE MailCopy DROP COLUMN DraftSyncAttemptCount");
            await legacyConnection.ExecuteAsync("ALTER TABLE MailCopy DROP COLUMN LastDraftSyncAttemptUtc");
            await legacyConnection.ExecuteAsync("ALTER TABLE MailCopy DROP COLUMN LastDraftSyncError");
            await MarkCompleted210Async(legacyConnection);
            await legacyConnection.CloseAsync();

            var configuration = new Mock<IApplicationConfiguration>();
            configuration.SetupProperty(x => x.PublisherSharedFolderPath, directory);
            databaseService = new DatabaseService(configuration.Object);

            await databaseService.InitializeAsync();

            var migratedLocal = await databaseService.Connection.FindAsync<MailCopy>(localDraft.UniqueId);
            var migratedNearMiss = await databaseService.Connection.FindAsync<MailCopy>(underscoreNearMiss.UniqueId);
            var escapedMatches = await databaseService.Connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM MailCopy WHERE IsDraft = 1 AND DraftId LIKE 'localDraft\\_%' ESCAPE '\\'");

            escapedMatches.Should().Be(1);
            migratedLocal.DraftSyncState.Should().Be(DraftSyncState.PendingSync);
            migratedNearMiss.DraftSyncState.Should().Be(DraftSyncState.Synced);

        }
        finally
        {
            if (databaseService?.Connection != null)
                await databaseService.Connection.CloseAsync();

            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_BackfillsAccountTaskSyncStateAndCreatesDeltaIndexesIdempotently()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wino-task-delta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, DatabaseService.CurrentDatabaseName);
        DatabaseService databaseService = null;
        var accountId = Guid.NewGuid();

        try
        {
            var legacy = new SQLiteAsyncConnection(databasePath);
            await legacy.CreateTableAsync<AccountTaskList>();
            await legacy.InsertAsync(new AccountTaskList
            {
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                SourceKind = TaskSourceKind.Outlook,
                RemoteId = "list",
                Title = "Tasks",
                ListDeltaLink = "graph-list-cursor",
                SubstrateGroupDeltaLink = "group-cursor",
                SubstrateFolderDeltaLink = "folder-cursor"
            });
            await MarkCompleted210Async(legacy);
            await legacy.CloseAsync();

            var configuration = new Mock<IApplicationConfiguration>();
            configuration.SetupProperty(item => item.PublisherSharedFolderPath, directory);
            databaseService = new DatabaseService(configuration.Object);

            await databaseService.InitializeAsync();
            await databaseService.InitializeAsync();

            var state = (await databaseService.Connection.Table<AccountTaskSyncState>().ToListAsync()).Should().ContainSingle().Which;
            state.MailAccountId.Should().Be(accountId);
            state.SourceKind.Should().Be(TaskSourceKind.Outlook);
            state.ListDeltaLink.Should().Be("graph-list-cursor");
            state.SubstrateGroupDeltaLink.Should().Be("group-cursor");
            state.SubstrateFolderDeltaLink.Should().Be("folder-cursor");

            var indexes = await databaseService.Connection.QueryAsync<IndexRow>("PRAGMA index_list('TaskSyncState')");
            indexes.Should().Contain(index => index.Name == "IX_TaskSyncState_Account_Source" && index.IsUnique == 1);
            (await databaseService.Connection.GetTableInfoAsync("TaskList"))
                .Should().Contain(column => column.Name == nameof(AccountTaskList.RemoteOrder));
        }
        finally
        {
            if (databaseService?.Connection != null)
                await databaseService.Connection.CloseAsync();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task MarkCompleted210Async(SQLiteAsyncConnection connection)
    {
        await connection.ExecuteAsync($"PRAGMA user_version = {DatabaseService.CurrentSchemaVersion};");
        await connection.ExecuteAsync(@"
CREATE TABLE __MigrationMetadata (
    Id INTEGER PRIMARY KEY NOT NULL,
    SourcePath TEXT,
    LastCompletedStep INTEGER NOT NULL,
    Status INTEGER NOT NULL,
    OptionsJson TEXT,
    RowCounts TEXT,
    DeferredAccountIds TEXT,
    UpdatedAtUtc TEXT NOT NULL
);");
        await connection.ExecuteAsync(
            "INSERT INTO __MigrationMetadata (Id, LastCompletedStep, Status, UpdatedAtUtc) VALUES (1, 10, ?, ?);",
            (int)Wino.Core.Domain.Models.Migration.MigrationStatus.Completed,
            DateTime.UtcNow);
    }

    private sealed class IndexRow
    {
        [Column("name")]
        public string Name { get; set; }

        [Column("unique")]
        public int IsUnique { get; set; }
    }
}
