#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using SQLite;
using Wino.Core.Domain.Entities.Intelligence;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Contracts.Intelligence;
using Wino.Messaging.UI;

namespace Wino.Services;

public sealed class LocalIntelligenceStore(
    IApplicationConfiguration applicationConfiguration,
    IMessenger messenger) : ILocalIntelligenceStore, IAsyncDisposable
{
    private const string DatabaseName = "WinoIntelligence.db";
    private const string V1EmbeddingEncoding = "float32-le";
    private const int V1EmbeddingDimensions = 768;
    private const int Float32ByteCount = sizeof(float);
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private SQLiteAsyncConnection? _connection;

    public bool DatabaseExists => File.Exists(GetDatabasePath());

    public async Task InitializeAsync()
    {
        if (_connection is not null)
            return;

        await _initializeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
                return;

            Directory.CreateDirectory(applicationConfiguration.ApplicationDataFolderPath);

            var path = Path.Combine(applicationConfiguration.ApplicationDataFolderPath, DatabaseName);
            var connection = new SQLiteAsyncConnection(path, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);
            await connection.CreateTableAsync<LocalIntelligenceDocumentRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<LocalMailboxStateRow>().ConfigureAwait(false);
            var mailboxColumns = await connection.GetTableInfoAsync("LocalMailboxState").ConfigureAwait(false);
            if (mailboxColumns.All(x => x.Name != nameof(LocalMailboxStateRow.IntelligenceVersion)))
                await connection.ExecuteAsync($"ALTER TABLE LocalMailboxState ADD COLUMN {nameof(LocalMailboxStateRow.IntelligenceVersion)} TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
            if (mailboxColumns.All(x => x.Name != nameof(LocalMailboxStateRow.IndexEpoch)))
                await connection.ExecuteAsync($"ALTER TABLE LocalMailboxState ADD COLUMN {nameof(LocalMailboxStateRow.IndexEpoch)} TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
            if (mailboxColumns.All(x => x.Name != nameof(LocalMailboxStateRow.HeadlineLanguage)))
                await connection.ExecuteAsync($"ALTER TABLE LocalMailboxState ADD COLUMN {nameof(LocalMailboxStateRow.HeadlineLanguage)} TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
            if (mailboxColumns.All(x => x.Name != nameof(LocalMailboxStateRow.SuppressHeadlineLanguagePrompt)))
                await connection.ExecuteAsync($"ALTER TABLE LocalMailboxState ADD COLUMN {nameof(LocalMailboxStateRow.SuppressHeadlineLanguagePrompt)} INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
            var documentColumns = await connection.GetTableInfoAsync("LocalIntelligenceDocument").ConfigureAwait(false);
            if (documentColumns.All(x => x.Name != nameof(LocalIntelligenceDocumentRow.IsDirectRecipient)))
                await connection.ExecuteAsync($"ALTER TABLE LocalIntelligenceDocument ADD COLUMN {nameof(LocalIntelligenceDocumentRow.IsDirectRecipient)} INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
            if (documentColumns.All(x => x.Name != nameof(LocalIntelligenceDocumentRow.HasLaterOutgoingReply)))
                await connection.ExecuteAsync($"ALTER TABLE LocalIntelligenceDocument ADD COLUMN {nameof(LocalIntelligenceDocumentRow.HasLaterOutgoingReply)} INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
            if (documentColumns.All(x => x.Name != nameof(LocalIntelligenceDocumentRow.Importance)))
                await connection.ExecuteAsync($"ALTER TABLE LocalIntelligenceDocument ADD COLUMN {nameof(LocalIntelligenceDocumentRow.Importance)} TEXT NOT NULL DEFAULT 'normal'").ConfigureAwait(false);
            if (documentColumns.All(x => x.Name != nameof(LocalIntelligenceDocumentRow.AttachmentMetadataJson)))
                await connection.ExecuteAsync($"ALTER TABLE LocalIntelligenceDocument ADD COLUMN {nameof(LocalIntelligenceDocumentRow.AttachmentMetadataJson)} TEXT NOT NULL DEFAULT '[]'").ConfigureAwait(false);
            await connection.ExecuteAsync("DROP TABLE IF EXISTS LocalArtifact").ConfigureAwait(false);
            await connection.ExecuteAsync("DROP TABLE IF EXISTS LocalBriefingHeadline").ConfigureAwait(false);
            await connection.ExecuteAsync("DROP TABLE IF EXISTS LocalMessageKey").ConfigureAwait(false);
            await connection.CreateTableAsync<LocalIntelligenceAccessRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<LocalAccountIntelligenceSnapshotRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<LocalDailyBriefingStateRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<LocalDailyBriefingIgnoreRow>().ConfigureAwait(false);
            await connection.ExecuteAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS IX_LocalIntelligenceDocument_Message " +
                "ON LocalIntelligenceDocument(LocalAccountId, ServerMessageKey)")
                .ConfigureAwait(false);
            _connection = connection;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<LocalIntelligenceMailboxState?> GetMailboxStateAsync(
        Guid localAccountId,
        CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var row = await lease.Connection.Table<LocalMailboxStateRow>()
            .Where(x => x.LocalAccountId == localAccountId)
            .FirstOrDefaultAsync().ConfigureAwait(false);

        if (row is null || string.IsNullOrWhiteSpace(row.IntelligenceVersion) ||
            !Guid.TryParse(row.IndexEpoch, out var indexEpoch))
        {
            return null;
        }

        return new LocalIntelligenceMailboxState(
            row.LocalAccountId,
            row.MailboxId,
            row.IntelligenceVersion,
            indexEpoch,
            row.LastImportedRevision);
    }

    public async Task AlignMailboxHeadAsync(
        Guid localAccountId,
        MailboxIntelligenceHeadDto head,
        CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        var existing = await connection.Table<LocalMailboxStateRow>()
            .Where(x => x.LocalAccountId == localAccountId)
            .FirstOrDefaultAsync().ConfigureAwait(false);
        var epoch = head.IndexEpoch.ToString("D");
        var resetDocuments = existing is null ||
            existing.MailboxId != head.MailboxId ||
            !string.Equals(existing.IntelligenceVersion, head.IntelligenceVersion, StringComparison.Ordinal) ||
            !string.Equals(existing.IndexEpoch, epoch, StringComparison.OrdinalIgnoreCase) ||
            existing.LastImportedRevision > head.ArtifactRevision;

        await connection.RunInTransactionAsync(transaction =>
        {
            if (resetDocuments)
            {
                transaction.Execute(
                    "DELETE FROM LocalIntelligenceDocument WHERE LocalAccountId = ?",
                    localAccountId);
            }

            transaction.InsertOrReplace(new LocalMailboxStateRow
            {
                LocalAccountId = localAccountId,
                MailboxId = head.MailboxId,
                IntelligenceVersion = head.IntelligenceVersion,
                IndexEpoch = epoch,
                LastImportedRevision = resetDocuments ? 0 : existing?.LastImportedRevision ?? 0,
                HeadlineLanguage = existing?.HeadlineLanguage ?? string.Empty,
                SuppressHeadlineLanguagePrompt = existing?.SuppressHeadlineLanguagePrompt ?? false,
                CoverageRulesJson = existing?.CoverageRulesJson ?? string.Empty,
                UpdatedAtUtc = DateTime.UtcNow,
            }, typeof(LocalMailboxStateRow));
        }).ConfigureAwait(false);

        if (resetDocuments)
        {
            messenger.Send(new IntelligenceMetadataChanged(
                localAccountId,
                new HashSet<string>(StringComparer.Ordinal),
                IntelligenceMetadataChangeScope.MailboxReset));
        }
    }

    public async Task<IReadOnlyDictionary<string, MessageIntelligenceDownloadDto>> GetCurrentDocumentsAsync(
        Guid localAccountId,
        IReadOnlyCollection<string> serverMessageKeys,
        CancellationToken cancellationToken = default)
    {
        var distinctKeys = serverMessageKeys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctKeys.Length == 0)
            return new Dictionary<string, MessageIntelligenceDownloadDto>(StringComparer.Ordinal);

        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<LocalIntelligenceDocumentRow>();
        foreach (var chunk in distinctKeys.Chunk(400))
        {
            var placeholders = string.Join(',', chunk.Select(static _ => "?"));
            var parameters = new object[] { localAccountId }.Concat(chunk.Cast<object>()).ToArray();
            rows.AddRange(await lease.Connection.QueryAsync<LocalIntelligenceDocumentRow>(
                $"SELECT * FROM LocalIntelligenceDocument WHERE LocalAccountId = ? AND ServerMessageKey IN ({placeholders})",
                parameters).ConfigureAwait(false));
        }

        return rows.ToDictionary(
            static row => row.ServerMessageKey,
            DeserializeDocument,
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<MessageIntelligenceDownloadDto>> GetCurrentDocumentsAsync(
        Guid localAccountId,
        CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var rows = await lease.Connection.Table<LocalIntelligenceDocumentRow>()
            .Where(row => row.LocalAccountId == localAccountId)
            .ToListAsync().ConfigureAwait(false);

        return rows
            .OrderBy(static row => row.ReceivedAtUtc)
            .ThenBy(static row => row.ServerMessageKey, StringComparer.Ordinal)
            .Select(DeserializeDocument)
            .ToArray();
    }

    public async Task<IReadOnlyList<LocalIntelligenceSearchDocument>> GetSearchDocumentsAsync(
        Guid localAccountId,
        CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var rows = await lease.Connection.Table<LocalIntelligenceDocumentRow>()
            .Where(row => row.LocalAccountId == localAccountId)
            .ToListAsync().ConfigureAwait(false);
        return rows.Select(row => new LocalIntelligenceSearchDocument(
            row.LocalAccountId, row.MailboxId, DeserializeDocument(row), row.Embedding)).ToArray();
    }

    public async Task<LocalIntelligenceChangeApplyResult> ApplyChangesAsync(
        Guid localAccountId,
        Guid mailboxId,
        IntelligenceChangesPageDto page,
        CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        var state = await connection.Table<LocalMailboxStateRow>()
            .Where(x => x.LocalAccountId == localAccountId)
            .FirstOrDefaultAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("The local intelligence mailbox state is not initialized.");
        if (state.MailboxId != mailboxId ||
            !string.Equals(state.IntelligenceVersion, page.IntelligenceVersion, StringComparison.Ordinal) ||
            !Guid.TryParse(state.IndexEpoch, out var stateEpoch) || stateEpoch != page.IndexEpoch)
        {
            throw new InvalidOperationException("The intelligence change page does not match the local mailbox head.");
        }
        if (page.NextAfterRevision < state.LastImportedRevision || page.ThroughRevision < page.NextAfterRevision)
            throw new InvalidOperationException("The intelligence change page revision is invalid.");

        var orderedChanges = page.Items.OrderBy(static item => item.Revision).ToArray();
        var previousRevision = state.LastImportedRevision;
        foreach (var change in orderedChanges)
        {
            if (string.IsNullOrWhiteSpace(change.ServerMessageKey) ||
                change.Revision <= previousRevision ||
                change.Revision > page.NextAfterRevision)
            {
                throw new InvalidOperationException("The intelligence change item revision is invalid.");
            }

            if (!change.IsDeleted &&
                (change.Document is null ||
                 !string.Equals(change.Document.ServerMessageKey, change.ServerMessageKey, StringComparison.Ordinal) ||
                 change.Document.ArtifactRevision != change.Revision))
            {
                throw new InvalidOperationException("The intelligence change document does not match its feed item.");
            }

            previousRevision = change.Revision;
        }

        var upserted = new HashSet<string>(StringComparer.Ordinal);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        await connection.RunInTransactionAsync(transaction =>
        {
            foreach (var change in orderedChanges)
            {
                if (change.IsDeleted)
                {
                    transaction.Execute(
                        "DELETE FROM LocalIntelligenceDocument WHERE LocalAccountId = ? AND ServerMessageKey = ?",
                        localAccountId,
                        change.ServerMessageKey);
                    deleted.Add(change.ServerMessageKey);
                    continue;
                }

                var document = change.Document
                    ?? throw new InvalidOperationException("An intelligence upsert change has no document.");
                var embedding = ValidateAndDecodeEmbedding(document);
                transaction.InsertOrReplace(ToRow(
                    localAccountId,
                    mailboxId,
                    page.IntelligenceVersion,
                    page.IndexEpoch,
                    document,
                    embedding), typeof(LocalIntelligenceDocumentRow));
                upserted.Add(change.ServerMessageKey);
            }

            state.LastImportedRevision = page.NextAfterRevision;
            state.UpdatedAtUtc = DateTime.UtcNow;
            transaction.InsertOrReplace(state, typeof(LocalMailboxStateRow));
        }).ConfigureAwait(false);

        var changed = upserted.Concat(deleted).ToHashSet(StringComparer.Ordinal);
        if (changed.Count > 0)
        {
            messenger.Send(new IntelligenceMetadataChanged(
                localAccountId,
                changed,
                IntelligenceMetadataChangeScope.Messages));
        }

        return new LocalIntelligenceChangeApplyResult(upserted, deleted, page.NextAfterRevision);
    }

    public async Task<string?> GetHeadlineLanguageAsync(Guid localAccountId, CancellationToken cancellationToken = default)
    {
        if (!DatabaseExists) return null;
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var row = await lease.Connection.Table<LocalMailboxStateRow>().Where(x => x.LocalAccountId == localAccountId).FirstOrDefaultAsync().ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(row?.HeadlineLanguage) ? null : row.HeadlineLanguage;
    }

    public async Task SetHeadlineLanguageAsync(Guid localAccountId, Guid mailboxId, string language, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var row = await lease.Connection.Table<LocalMailboxStateRow>().Where(x => x.LocalAccountId == localAccountId).FirstOrDefaultAsync().ConfigureAwait(false)
            ?? new LocalMailboxStateRow { LocalAccountId = localAccountId, MailboxId = mailboxId };
        var languageChanged = !string.Equals(row.HeadlineLanguage, language, StringComparison.OrdinalIgnoreCase);
        row.MailboxId = mailboxId;
        row.HeadlineLanguage = language;
        if (languageChanged)
            row.SuppressHeadlineLanguagePrompt = false;
        row.UpdatedAtUtc = DateTime.UtcNow;
        await lease.Connection.InsertOrReplaceAsync(row, typeof(LocalMailboxStateRow)).ConfigureAwait(false);
    }

    public async Task<bool> GetHeadlineLanguagePromptSuppressedAsync(Guid localAccountId, CancellationToken cancellationToken = default)
    {
        if (!DatabaseExists) return false;
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var row = await lease.Connection.Table<LocalMailboxStateRow>().Where(x => x.LocalAccountId == localAccountId).FirstOrDefaultAsync().ConfigureAwait(false);
        return row?.SuppressHeadlineLanguagePrompt == true;
    }

    public async Task SetHeadlineLanguagePromptSuppressedAsync(Guid localAccountId, bool suppressed, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var row = await lease.Connection.Table<LocalMailboxStateRow>().Where(x => x.LocalAccountId == localAccountId).FirstOrDefaultAsync().ConfigureAwait(false);
        if (row is null) return;
        row.SuppressHeadlineLanguagePrompt = suppressed;
        row.UpdatedAtUtc = DateTime.UtcNow;
        await lease.Connection.InsertOrReplaceAsync(row, typeof(LocalMailboxStateRow)).ConfigureAwait(false);
    }

    public async Task SaveAccessSnapshotAsync(LocalIntelligenceAccessSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        await lease.Connection.InsertOrReplaceAsync(new LocalIntelligenceAccessRow
        {
            LocalAccountId = snapshot.LocalAccountId,
            WinoAccountId = snapshot.WinoAccountId,
            HasAiPack = snapshot.HasAiPack,
            HasIntelligenceConsent = snapshot.HasIntelligenceConsent,
            MailboxId = snapshot.MailboxId,
            UpdatedAtUtc = snapshot.UpdatedAtUtc.UtcDateTime,
        }, typeof(LocalIntelligenceAccessRow)).ConfigureAwait(false);
    }

    public async Task<LocalIntelligenceAccessSnapshot?> GetAccessSnapshotAsync(Guid localAccountId, CancellationToken cancellationToken = default)
    {
        if (!DatabaseExists) return null;
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var row = await lease.Connection.Table<LocalIntelligenceAccessRow>()
            .Where(x => x.LocalAccountId == localAccountId).FirstOrDefaultAsync().ConfigureAwait(false);
        return row is null ? null : new(row.LocalAccountId, row.WinoAccountId, row.HasAiPack,
            row.HasIntelligenceConsent, row.MailboxId, ToOffset(row.UpdatedAtUtc));
    }

    public async Task DeleteAccessSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        if (!DatabaseExists) return;
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        await lease.Connection.DeleteAllAsync<LocalIntelligenceAccessRow>().ConfigureAwait(false);
    }

    public async Task SaveAccountIntelligenceSnapshotAsync(WinoAccountIntelligenceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        await lease.Connection.InsertOrReplaceAsync(new LocalAccountIntelligenceSnapshotRow
        {
            WinoAccountId = snapshot.WinoAccountId,
            Payload = JsonSerializer.Serialize(snapshot, LocalIntelligenceJsonContext.Default.WinoAccountIntelligenceSnapshot),
            UpdatedAtUtc = DateTime.UtcNow
        }, typeof(LocalAccountIntelligenceSnapshotRow)).ConfigureAwait(false);
    }

    public async Task<WinoAccountIntelligenceSnapshot?> GetAccountIntelligenceSnapshotAsync(Guid winoAccountId, CancellationToken cancellationToken = default)
    {
        if (!DatabaseExists) return null;
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var row = await lease.Connection.Table<LocalAccountIntelligenceSnapshotRow>()
            .Where(x => x.WinoAccountId == winoAccountId).FirstOrDefaultAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(row?.Payload)) return null;
        try
        {
            return JsonSerializer.Deserialize(row.Payload, LocalIntelligenceJsonContext.Default.WinoAccountIntelligenceSnapshot);
        }
        catch (JsonException)
        {
            // A cache schema can evolve independently of intelligence artifacts. Treat an
            // unreadable snapshot as a cold cache and let background revalidation replace it.
            await lease.Connection.ExecuteAsync(
                "DELETE FROM LocalAccountIntelligenceSnapshot WHERE WinoAccountId = ?", winoAccountId).ConfigureAwait(false);
            return null;
        }
    }

    public async Task DeleteAccountIntelligenceSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        if (!DatabaseExists) return;
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        await lease.Connection.DeleteAllAsync<LocalAccountIntelligenceSnapshotRow>().ConfigureAwait(false);
    }

    public async Task<long> GetLatestBriefingFactRevisionAsync(Guid localAccountId, CancellationToken cancellationToken = default)
    {
        if (!DatabaseExists) return 0;
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        return await GetLatestBriefingFactRevisionAsync(lease.Connection, localAccountId).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, long>> GetDailyBriefingIgnoreRevisionsAsync(
        Guid localAccountId, CancellationToken cancellationToken = default)
    {
        if (!DatabaseExists) return new Dictionary<Guid, long>();

        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var rows = await lease.Connection.Table<LocalDailyBriefingIgnoreRow>()
            .Where(x => x.LocalAccountId == localAccountId)
            .ToListAsync().ConfigureAwait(false);
        return rows.ToDictionary(static x => x.BriefingId, static x => x.IgnoredArtifactRevision);
    }

    public async Task SaveDailyBriefingIgnoreAsync(
        Guid localAccountId,
        Guid briefingId,
        long artifactRevision,
        DateTimeOffset ignoredAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (briefingId == Guid.Empty)
            return;

        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        await lease.Connection.InsertOrReplaceAsync(new LocalDailyBriefingIgnoreRow
        {
            Key = DailyBriefingIgnoreKey(localAccountId, briefingId),
            LocalAccountId = localAccountId,
            BriefingId = briefingId,
            IgnoredArtifactRevision = artifactRevision,
            IgnoredAtUtc = ignoredAtUtc.UtcDateTime,
        }, typeof(LocalDailyBriefingIgnoreRow)).ConfigureAwait(false);
    }

    public async Task DeleteDailyBriefingIgnoreAsync(
        Guid localAccountId,
        Guid briefingId,
        CancellationToken cancellationToken = default)
    {
        if (briefingId == Guid.Empty)
            return;

        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        await lease.Connection.ExecuteAsync(
            "DELETE FROM LocalDailyBriefingIgnore WHERE LocalAccountId = ? AND BriefingId = ?",
            localAccountId, briefingId).ConfigureAwait(false);
    }

    public async Task DeleteDailyBriefingItemAsync(
        Guid localAccountId,
        string remoteMessageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteMessageId);

        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        await lease.Connection.ExecuteAsync(
            "DELETE FROM LocalIntelligenceDocument WHERE LocalAccountId = ? AND ServerMessageKey = ?",
            localAccountId, remoteMessageId).ConfigureAwait(false);
    }

    public async Task<DailyBriefingUnseenState> GetDailyBriefingUnseenStateAsync(IReadOnlyCollection<Guid> localAccountIds, CancellationToken cancellationToken = default)
    {
        if (localAccountIds.Count == 0 || !DatabaseExists) return new(false, null);
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        DateTime? latestOpened = null;
        foreach (var accountId in localAccountIds)
        {
            var state = await lease.Connection.Table<LocalDailyBriefingStateRow>()
                .Where(x => x.LocalAccountId == accountId).FirstOrDefaultAsync().ConfigureAwait(false);
            var latestFact = await GetLatestBriefingFactRevisionAsync(lease.Connection, accountId).ConfigureAwait(false);
            if (latestFact > (state?.LastViewedFactRevision ?? 0))
                return new(true, state?.LastOpenedAtUtc is DateTime opened ? ToOffset(opened) : null);
            if (state?.LastOpenedAtUtc is DateTime candidate && (latestOpened is null || candidate > latestOpened)) latestOpened = candidate;
        }
        return new(false, latestOpened is DateTime value ? ToOffset(value) : null);
    }

    public Task MarkDailyBriefingOpenedAsync(IReadOnlyCollection<Guid> localAccountIds, DateTimeOffset openedAtUtc, CancellationToken cancellationToken = default)
        => UpdateBriefingStateAsync(localAccountIds, openedAtUtc, false, cancellationToken);

    public Task MarkDailyBriefingViewedAsync(IReadOnlyCollection<Guid> localAccountIds, DateTimeOffset viewedAtUtc, CancellationToken cancellationToken = default)
        => UpdateBriefingStateAsync(localAccountIds, viewedAtUtc, true, cancellationToken);

    private async Task UpdateBriefingStateAsync(IReadOnlyCollection<Guid> localAccountIds, DateTimeOffset timestamp, bool viewed, CancellationToken cancellationToken)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        foreach (var accountId in localAccountIds)
        {
            var state = await lease.Connection.Table<LocalDailyBriefingStateRow>()
                .Where(x => x.LocalAccountId == accountId).FirstOrDefaultAsync().ConfigureAwait(false)
                ?? new LocalDailyBriefingStateRow { LocalAccountId = accountId };
            state.LastOpenedAtUtc = timestamp.UtcDateTime;
            if (viewed)
            {
                state.LastViewedAtUtc = timestamp.UtcDateTime;
                state.LastViewedFactRevision = await GetLatestBriefingFactRevisionAsync(lease.Connection, accountId).ConfigureAwait(false);
            }
            await lease.Connection.InsertOrReplaceAsync(state, typeof(LocalDailyBriefingStateRow)).ConfigureAwait(false);
        }
    }

    private static Task<long> GetLatestBriefingFactRevisionAsync(SQLiteAsyncConnection connection, Guid accountId)
        => connection.ExecuteScalarAsync<long>(
            "SELECT COALESCE(MAX(ArtifactRevision), 0) FROM LocalIntelligenceDocument WHERE LocalAccountId = ?",
            accountId);

    private static DateTimeOffset ToOffset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string DailyBriefingIgnoreKey(Guid localAccountId, Guid briefingId)
        => $"{localAccountId:D}|{briefingId:D}";

    public async Task DeleteMailboxAsync(Guid localAccountId, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await connection.RunInTransactionAsync(transaction =>
        {
            transaction.Execute("DELETE FROM LocalIntelligenceDocument WHERE LocalAccountId = ?", localAccountId);
            transaction.Execute("DELETE FROM LocalMailboxState WHERE LocalAccountId = ?", localAccountId);
            transaction.Execute("DELETE FROM LocalIntelligenceAccess WHERE LocalAccountId = ?", localAccountId);
            transaction.Execute("DELETE FROM LocalDailyBriefingState WHERE LocalAccountId = ?", localAccountId);
            transaction.Execute("DELETE FROM LocalDailyBriefingIgnore WHERE LocalAccountId = ?", localAccountId);
        }).ConfigureAwait(false);
        messenger.Send(new IntelligenceMetadataChanged(
            localAccountId,
            new HashSet<string>(StringComparer.Ordinal),
            IntelligenceMetadataChangeScope.MailboxReset));
    }

    public async Task DeleteDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _initializeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_connection is not null)
                {
                    await _connection.CloseAsync().ConfigureAwait(false);
                    _connection = null;
                }

                foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
                {
                    var path = GetDatabasePath() + suffix;
                    if (File.Exists(path))
                        File.Delete(path);
                }
            }
            finally
            {
                _initializeLock.Release();
            }
        }
        finally
        {
            _operationLock.Release();
        }

        messenger.Send(new IntelligenceMetadataChanged(
            null,
            new HashSet<string>(StringComparer.Ordinal),
            IntelligenceMetadataChangeScope.DatabaseReset));
    }

    public async ValueTask DisposeAsync()
    {
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
                await _connection.CloseAsync().ConfigureAwait(false);
            _connection = null;
        }
        finally
        {
            _operationLock.Release();
        }
        _initializeLock.Dispose();
        _operationLock.Dispose();
    }

    private async Task<ConnectionLease> GetConnectionLeaseAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = _connection
                ?? throw new InvalidOperationException("The local intelligence store has not been initialized.");

            return new ConnectionLease(connection, _operationLock);
        }
        catch
        {
            _operationLock.Release();
            throw;
        }
    }

    private string GetDatabasePath()
        => Path.Combine(applicationConfiguration.ApplicationDataFolderPath, DatabaseName);

    private static byte[] ValidateAndDecodeEmbedding(MessageIntelligenceDownloadDto document)
    {
        if (document.EmbeddingDimensions != V1EmbeddingDimensions ||
            !string.Equals(document.EmbeddingEncoding, V1EmbeddingEncoding, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The intelligence document uses an unsupported embedding format.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(document.Embedding);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The intelligence document embedding is not valid base64.", exception);
        }

        if (bytes.Length != V1EmbeddingDimensions * Float32ByteCount)
            throw new InvalidOperationException("The intelligence document embedding has an invalid byte length.");

        return bytes;
    }

    private static LocalIntelligenceDocumentRow ToRow(
        Guid localAccountId,
        Guid mailboxId,
        string intelligenceVersion,
        Guid indexEpoch,
        MessageIntelligenceDownloadDto document,
        byte[] embedding)
        => new()
        {
            Key = $"{localAccountId:D}|{document.ServerMessageKey}",
            LocalAccountId = localAccountId,
            MailboxId = mailboxId,
            ServerMessageKey = document.ServerMessageKey,
            IntelligenceVersion = intelligenceVersion,
            IndexEpoch = indexEpoch.ToString("D"),
            ContentHash = document.ContentHash,
            Subject = document.Subject,
            Sender = document.Sender,
            ReceivedAtUtc = document.ReceivedAtUtc.UtcDateTime,
            IsOutgoing = document.IsOutgoing,
            IsRead = document.IsRead,
            IsFlagged = document.IsFlagged,
            HasAttachments = document.HasAttachments,
            IsDirectRecipient = document.IsDirectRecipient,
            HasLaterOutgoingReply = document.HasLaterOutgoingReply,
            Importance = document.Importance,
            AttachmentMetadataJson = JsonSerializer.Serialize(document.Attachments.ToArray(), LocalIntelligenceJsonContext.Default.MessageAttachmentMetadataV1Array),
            FolderIdsJson = JsonSerializer.Serialize(document.FolderIds.ToArray(), WinoAccountApiJsonContext.Default.StringArray),
            SenderAddressesJson = JsonSerializer.Serialize(document.SenderAddresses.ToArray(), WinoAccountApiJsonContext.Default.StringArray),
            RecipientAddressesJson = JsonSerializer.Serialize(document.RecipientAddresses.ToArray(), WinoAccountApiJsonContext.Default.StringArray),
            AnalysisJson = JsonSerializer.Serialize(
                document.Analysis,
                WinoAccountApiJsonContext.Default.MessageIntelligenceDocumentV1),
            Embedding = embedding,
            EmbeddingDimensions = document.EmbeddingDimensions,
            EmbeddingEncoding = document.EmbeddingEncoding,
            ArtifactRevision = document.ArtifactRevision,
            GeneratedAtUtc = document.GeneratedAtUtc.UtcDateTime,
        };

    private static MessageIntelligenceDownloadDto DeserializeDocument(LocalIntelligenceDocumentRow row)
        => new()
        {
            ServerMessageKey = row.ServerMessageKey,
            ContentHash = row.ContentHash,
            Subject = row.Subject,
            Sender = row.Sender,
            ReceivedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(row.ReceivedAtUtc, DateTimeKind.Utc)),
            IsOutgoing = row.IsOutgoing,
            IsRead = row.IsRead,
            IsFlagged = row.IsFlagged,
            HasAttachments = row.HasAttachments,
            IsDirectRecipient = row.IsDirectRecipient,
            HasLaterOutgoingReply = row.HasLaterOutgoingReply,
            Importance = row.Importance,
            Attachments = JsonSerializer.Deserialize(row.AttachmentMetadataJson, LocalIntelligenceJsonContext.Default.MessageAttachmentMetadataV1Array) ?? [],
            FolderIds = JsonSerializer.Deserialize(row.FolderIdsJson, WinoAccountApiJsonContext.Default.StringArray) ?? [],
            SenderAddresses = JsonSerializer.Deserialize(row.SenderAddressesJson, WinoAccountApiJsonContext.Default.StringArray) ?? [],
            RecipientAddresses = JsonSerializer.Deserialize(row.RecipientAddressesJson, WinoAccountApiJsonContext.Default.StringArray) ?? [],
            Analysis = JsonSerializer.Deserialize(
                row.AnalysisJson,
                WinoAccountApiJsonContext.Default.MessageIntelligenceDocumentV1)
                ?? throw new JsonException("Stored intelligence analysis is empty."),
            Embedding = Convert.ToBase64String(row.Embedding),
            EmbeddingDimensions = row.EmbeddingDimensions,
            EmbeddingEncoding = row.EmbeddingEncoding,
            ArtifactRevision = row.ArtifactRevision,
            GeneratedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(row.GeneratedAtUtc, DateTimeKind.Utc)),
        };
    private sealed class ConnectionLease(SQLiteAsyncConnection connection, SemaphoreSlim operationLock) : IDisposable
    {
        public SQLiteAsyncConnection Connection { get; } = connection;

        public void Dispose() => operationLock.Release();
    }
}
