using FluentAssertions;
using Moq;
using SQLite;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class DatabaseMigrationTests
{
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
