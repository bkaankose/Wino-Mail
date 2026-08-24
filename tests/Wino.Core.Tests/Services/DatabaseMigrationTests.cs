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
    public async Task InitializeAsync_DiscardsLegacyContactsAndCreatesAccountScopedSchemaIdempotently()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wino-contact-schema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "Wino200.db");
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
            await legacy.CloseAsync();

            var configuration = new Mock<IApplicationConfiguration>();
            configuration.SetupProperty(x => x.PublisherSharedFolderPath, directory);
            databaseService = new DatabaseService(configuration.Object);
            await databaseService.InitializeAsync();
            await databaseService.InitializeAsync();

            (await databaseService.Connection.GetTableInfoAsync("AccountContact")).Should().BeEmpty();
            (await databaseService.Connection.GetTableInfoAsync("ContactGroup")).Should().BeEmpty();
            (await databaseService.Connection.GetTableInfoAsync("ContactCard")).Should().NotBeEmpty();
            (await databaseService.Connection.GetTableInfoAsync("ContactEmailAddress")).Should().NotBeEmpty();
            (await databaseService.Connection.Table<ContactAddressBook>().Where(book => book.MailAccountId == accountId).CountAsync()).Should().Be(1);
            (await databaseService.Connection.FindAsync<MailAccount>(accountId)).IsContactAccessGranted.Should().BeFalse();
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
        var databasePath = Path.Combine(directory, "Wino200.db");
        DatabaseService databaseService = null;

        try
        {
            var legacyConnection = new SQLiteAsyncConnection(databasePath);
            await legacyConnection.CreateTableAsync<MailAccount>();
            await legacyConnection.ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} DROP COLUMN {nameof(MailAccount.ProfilePictureFileId)}");
            await legacyConnection.ExecuteAsync($"ALTER TABLE {nameof(MailAccount)} DROP COLUMN {nameof(MailAccount.IsProfilePictureBackfillComplete)}");
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
        var databasePath = Path.Combine(directory, "Wino200.db");
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
        var databasePath = Path.Combine(directory, "Wino200.db");
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
}
