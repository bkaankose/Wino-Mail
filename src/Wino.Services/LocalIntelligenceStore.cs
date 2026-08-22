#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using SQLite;
using Wino.Core.Domain.Entities.Intelligence;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Contracts.Intelligence;
using Wino.Messaging.UI;

namespace Wino.Services;

public sealed class LocalIntelligenceStore(
    IApplicationConfiguration applicationConfiguration,
    IMessenger messenger) : ILocalIntelligenceStore, IAsyncDisposable
{
    private const string DatabaseName = "WinoIntelligence.db";
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
            await connection.CreateTableAsync<LocalArtifactRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<LocalMailboxStateRow>().ConfigureAwait(false);
            var mailboxColumns = await connection.GetTableInfoAsync("LocalMailboxState").ConfigureAwait(false);
            if (mailboxColumns.All(x => x.Name != nameof(LocalMailboxStateRow.HeadlineLanguage)))
                await connection.ExecuteAsync($"ALTER TABLE LocalMailboxState ADD COLUMN {nameof(LocalMailboxStateRow.HeadlineLanguage)} TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
            if (mailboxColumns.All(x => x.Name != nameof(LocalMailboxStateRow.SuppressHeadlineLanguagePrompt)))
                await connection.ExecuteAsync($"ALTER TABLE LocalMailboxState ADD COLUMN {nameof(LocalMailboxStateRow.SuppressHeadlineLanguagePrompt)} INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
            await connection.CreateTableAsync<LocalBriefingHeadlineRow>().ConfigureAwait(false);
            var headlineColumns = await connection.GetTableInfoAsync("LocalBriefingHeadline").ConfigureAwait(false);
            if (headlineColumns.All(x => x.Name != nameof(LocalBriefingHeadlineRow.RemoteMessageId)))
                await connection.ExecuteAsync($"ALTER TABLE LocalBriefingHeadline ADD COLUMN {nameof(LocalBriefingHeadlineRow.RemoteMessageId)} TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
            if (headlineColumns.All(x => x.Name != nameof(LocalBriefingHeadlineRow.ContentHash)))
                await connection.ExecuteAsync($"ALTER TABLE LocalBriefingHeadline ADD COLUMN {nameof(LocalBriefingHeadlineRow.ContentHash)} TEXT NOT NULL DEFAULT ''").ConfigureAwait(false);
            if (headlineColumns.All(x => x.Name != nameof(LocalBriefingHeadlineRow.GenerationVersion)))
                await connection.ExecuteAsync($"ALTER TABLE LocalBriefingHeadline ADD COLUMN {nameof(LocalBriefingHeadlineRow.GenerationVersion)} INTEGER NOT NULL DEFAULT 0").ConfigureAwait(false);
            await connection.ExecuteAsync("DROP TABLE IF EXISTS LocalMessageKey").ConfigureAwait(false);
            await connection.CreateTableAsync<LocalIntelligenceAccessRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<LocalAccountIntelligenceSnapshotRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<LocalDailyBriefingStateRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<LocalDailyBriefingIgnoreRow>().ConfigureAwait(false);
            await connection.ExecuteAsync(
                "CREATE INDEX IF NOT EXISTS IX_LocalArtifact_Current " +
                "ON LocalArtifact(LocalAccountId, RemoteMessageId, CapabilityId, ArtifactRevision DESC)")
                .ConfigureAwait(false);
            _connection = connection;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<IReadOnlyList<IntelligenceArtifactDto>> GetCurrentArtifactsAsync(
        Guid localAccountId,
        string remoteMessageId,
        CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        var rows = await connection.Table<LocalArtifactRow>()
            .Where(x => x.LocalAccountId == localAccountId && x.RemoteMessageId == remoteMessageId)
            .ToListAsync().ConfigureAwait(false);
        return rows.GroupBy(x => x.CapabilityId, StringComparer.Ordinal)
            .Select(group => group.MaxBy(x => x.ArtifactRevision)!)
            .Select(DeserializeStoredArtifact)
            .ToArray();
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<IntelligenceArtifactDto>>> GetCurrentArtifactsAsync(
        Guid localAccountId,
        IReadOnlyCollection<string> remoteMessageIds,
        CancellationToken cancellationToken = default)
    {
        var distinctIds = remoteMessageIds?
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (distinctIds.Length == 0)
            return new Dictionary<string, IReadOnlyList<IntelligenceArtifactDto>>(StringComparer.Ordinal);

        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<LocalArtifactRow>();
        const int chunkSize = 400;
        foreach (var chunk in distinctIds.Chunk(chunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var placeholders = string.Join(',', chunk.Select(static _ => "?"));
            var parameters = new object[] { localAccountId }.Concat(chunk.Cast<object>()).ToArray();
            rows.AddRange(await lease.Connection.QueryAsync<LocalArtifactRow>(
                $"SELECT artifact.* FROM LocalArtifact artifact " +
                $"WHERE artifact.LocalAccountId = ? AND artifact.RemoteMessageId IN ({placeholders}) " +
                "AND NOT EXISTS (" +
                "SELECT 1 FROM LocalArtifact newer " +
                "WHERE newer.LocalAccountId = artifact.LocalAccountId " +
                "AND newer.RemoteMessageId = artifact.RemoteMessageId " +
                "AND newer.CapabilityId = artifact.CapabilityId " +
                "AND newer.ArtifactRevision > artifact.ArtifactRevision)",
                parameters).ConfigureAwait(false));
        }

        var result = new Dictionary<string, IReadOnlyList<IntelligenceArtifactDto>>(StringComparer.Ordinal);
        foreach (var messageGroup in rows.GroupBy(static row => row.RemoteMessageId, StringComparer.Ordinal))
        {
            var artifacts = new List<IntelligenceArtifactDto>();
            foreach (var latest in messageGroup)
            {
                try
                {
                    artifacts.Add(DeserializeStoredArtifact(latest));
                }
                catch (Exception exception) when (exception is JsonException or NotSupportedException)
                {
                    // A malformed capability must not prevent the rest of the mail list from loading.
                }
            }

            if (artifacts.Count > 0)
                result[messageGroup.Key] = artifacts;
        }

        return result;
    }

    public async Task UpsertArtifactsAsync(
        Guid localAccountId,
        Guid mailboxId,
        IReadOnlyList<IntelligenceArtifactDto> artifacts,
        CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        var existingState = await connection.Table<LocalMailboxStateRow>()
            .Where(x => x.LocalAccountId == localAccountId).FirstOrDefaultAsync().ConfigureAwait(false);
        await connection.RunInTransactionAsync(transaction =>
        {
            foreach (var artifact in artifacts)
            {
                if (artifact.Capability == IntelligenceCapability.BriefingHeadline)
                {
                    if (artifact.IsDeleted)
                    {
                        transaction.Execute(
                            "DELETE FROM LocalBriefingHeadline WHERE LocalAccountId = ? AND RemoteMessageId = ?",
                            localAccountId,
                            artifact.RemoteMessageId);
                    }
                    else if (artifact.BriefingHeadline is { } headline)
                    {
                        var key = $"{localAccountId:D}|{headline.BriefingId:D}";
                        transaction.InsertOrReplace(new LocalBriefingHeadlineRow
                        {
                            Key = key,
                            LocalAccountId = localAccountId,
                            MailboxId = mailboxId,
                            RemoteMessageId = artifact.RemoteMessageId,
                            ContentHash = artifact.ContentHash,
                            GenerationVersion = artifact.GenerationVersion,
                            BriefingId = headline.BriefingId,
                            Headline = headline.Headline,
                            ArtifactRevision = artifact.ArtifactRevision,
                            UpdatedAtUtc = artifact.GeneratedAtUtc.UtcDateTime,
                        }, typeof(LocalBriefingHeadlineRow));
                    }
                    continue;
                }
                transaction.InsertOrReplace(new LocalArtifactRow
                {
                    Key = $"{localAccountId:D}|{artifact.RemoteMessageId}|{artifact.CapabilityId}|{artifact.ArtifactRevision}",
                    LocalAccountId = localAccountId,
                    MailboxId = mailboxId,
                    RemoteMessageId = artifact.RemoteMessageId,
                    ContentHash = artifact.ContentHash,
                    CapabilityId = artifact.CapabilityId,
                    GenerationVersion = artifact.GenerationVersion,
                    PayloadSchemaVersion = artifact.PayloadSchemaVersion,
                    ArtifactRevision = artifact.ArtifactRevision,
                    GeneratedAtUtc = artifact.GeneratedAtUtc.UtcDateTime,
                    Confidence = artifact.Confidence,
                    IsDeleted = artifact.IsDeleted,
                    PayloadJson = SerializeStoredArtifact(artifact),
                }, typeof(LocalArtifactRow));
            }

            transaction.InsertOrReplace(new LocalMailboxStateRow
            {
                LocalAccountId = localAccountId,
                MailboxId = mailboxId,
                LastImportedRevision = existingState?.LastImportedRevision ?? 0,
                HeadlineLanguage = existingState?.HeadlineLanguage ?? string.Empty,
                SuppressHeadlineLanguagePrompt = existingState?.SuppressHeadlineLanguagePrompt ?? false,
                UpdatedAtUtc = DateTime.UtcNow,
            }, typeof(LocalMailboxStateRow));
        }).ConfigureAwait(false);
        await CompactHistoryAsync(connection).ConfigureAwait(false);
        var changedIds = artifacts
            .Select(static artifact => artifact.RemoteMessageId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (changedIds.Count > 0)
        {
            messenger.Send(new IntelligenceMetadataChanged(
                localAccountId,
                changedIds,
                IntelligenceMetadataChangeScope.Messages));
        }
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

    public async Task<IReadOnlyDictionary<Guid, string>> GetBriefingHeadlinesAsync(Guid localAccountId, IReadOnlyCollection<Guid> briefingIds, CancellationToken cancellationToken = default)
    {
        if (briefingIds.Count == 0 || !DatabaseExists) return new Dictionary<Guid, string>();
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var wanted = briefingIds.ToHashSet();
        var rows = await lease.Connection.Table<LocalBriefingHeadlineRow>().Where(x => x.LocalAccountId == localAccountId).ToListAsync().ConfigureAwait(false);
        return rows.Where(x => wanted.Contains(x.BriefingId)).ToDictionary(x => x.BriefingId, x => x.Headline);
    }

    public async Task ApplyBriefingHeadlineUpdatesAsync(Guid localAccountId, Guid mailboxId, string language, IReadOnlyList<BriefingHeadlineUpdateDto> headlines, long throughRevision, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        var state = await connection.Table<LocalMailboxStateRow>().Where(x => x.LocalAccountId == localAccountId).FirstOrDefaultAsync().ConfigureAwait(false)
            ?? new LocalMailboxStateRow { LocalAccountId = localAccountId, MailboxId = mailboxId };
        var existingHeadlines = (await connection.Table<LocalBriefingHeadlineRow>()
            .Where(x => x.LocalAccountId == localAccountId)
            .ToListAsync().ConfigureAwait(false))
            .ToDictionary(x => x.BriefingId);
        await connection.RunInTransactionAsync(transaction =>
        {
            foreach (var headline in headlines)
            {
                existingHeadlines.TryGetValue(headline.BriefingId, out var existing);
                transaction.InsertOrReplace(new LocalBriefingHeadlineRow
                {
                    Key = $"{localAccountId:D}|{headline.BriefingId:D}",
                    LocalAccountId = localAccountId,
                    MailboxId = mailboxId,
                    RemoteMessageId = existing?.RemoteMessageId ?? string.Empty,
                    ContentHash = existing?.ContentHash ?? string.Empty,
                    GenerationVersion = existing?.GenerationVersion ?? 0,
                    BriefingId = headline.BriefingId,
                    Headline = headline.Headline,
                    ArtifactRevision = headline.ArtifactRevision,
                    UpdatedAtUtc = headline.UpdatedAtUtc.UtcDateTime,
                }, typeof(LocalBriefingHeadlineRow));
            }
            state.MailboxId = mailboxId;
            state.HeadlineLanguage = language;
            state.SuppressHeadlineLanguagePrompt = false;
            state.LastImportedRevision = Math.Max(state.LastImportedRevision, throughRevision);
            state.UpdatedAtUtc = DateTime.UtcNow;
            transaction.InsertOrReplace(state, typeof(LocalMailboxStateRow));
        }).ConfigureAwait(false);
        messenger.Send(new IntelligenceMetadataChanged(
            localAccountId,
            new HashSet<string>(),
            IntelligenceMetadataChangeScope.Messages));
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
        var briefingCapabilityId = IntelligenceCapabilityIds.GetStorageId(IntelligenceCapability.BriefingFact);
        await lease.Connection.RunInTransactionAsync(transaction =>
        {
            transaction.Execute(
                "UPDATE LocalArtifact SET IsDeleted = 1 WHERE LocalAccountId = ? AND RemoteMessageId = ? AND CapabilityId = ?",
                localAccountId, remoteMessageId, briefingCapabilityId);
        }).ConfigureAwait(false);
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
            "SELECT COALESCE(MAX(ArtifactRevision), 0) FROM LocalArtifact WHERE LocalAccountId = ? AND CapabilityId = ?",
            accountId, IntelligenceCapabilityIds.GetStorageId(IntelligenceCapability.BriefingFact));

    private static DateTimeOffset ToOffset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string DailyBriefingIgnoreKey(Guid localAccountId, Guid briefingId)
        => $"{localAccountId:D}|{briefingId:D}";

    public async Task DeleteMailboxAsync(Guid localAccountId, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await connection.RunInTransactionAsync(transaction =>
        {
            transaction.Execute("DELETE FROM LocalArtifact WHERE LocalAccountId = ?", localAccountId);
            transaction.Execute("DELETE FROM LocalBriefingHeadline WHERE LocalAccountId = ?", localAccountId);
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

    private static async Task CompactHistoryAsync(SQLiteAsyncConnection connection)
    {
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var oldRows = await connection.Table<LocalArtifactRow>().Where(x => x.GeneratedAtUtc < cutoff).ToListAsync().ConfigureAwait(false);
        foreach (var row in oldRows)
        {
            var hasNewer = await connection.Table<LocalArtifactRow>().Where(x =>
                x.LocalAccountId == row.LocalAccountId &&
                x.RemoteMessageId == row.RemoteMessageId &&
                x.CapabilityId == row.CapabilityId &&
                x.ArtifactRevision > row.ArtifactRevision).CountAsync().ConfigureAwait(false) > 0;
            if (hasNewer)
                await connection.ExecuteAsync("DELETE FROM LocalArtifact WHERE Key = ?", row.Key).ConfigureAwait(false);
        }
    }

    private static IntelligenceArtifactDto DeserializeStoredArtifact(LocalArtifactRow row)
    {
        if (!row.IsDeleted)
            return DeserializeTypedArtifact(row.PayloadJson);

        var payload = JsonNode.Parse(row.PayloadJson)?.AsObject()
            ?? throw new JsonException("Stored intelligence artifact is empty.");
        payload["IsDeleted"] = true;
        return JsonSerializer.Deserialize(
            payload.ToJsonString(), WinoAccountApiJsonContext.Default.IntelligenceArtifactDto)
            ?? throw new JsonException("Stored intelligence artifact is empty.");
    }

    internal static string SerializeStoredArtifact(IntelligenceArtifactDto artifact)
        => JsonSerializer.Serialize(artifact, WinoAccountApiJsonContext.Default.IntelligenceArtifactDto);

    internal static IntelligenceArtifactDto DeserializeTypedArtifact(string json)
        => JsonSerializer.Deserialize(json, WinoAccountApiJsonContext.Default.IntelligenceArtifactDto)
            ?? throw new JsonException("Stored intelligence artifact is empty.");
    private sealed class ConnectionLease(SQLiteAsyncConnection connection, SemaphoreSlim operationLock) : IDisposable
    {
        public SQLiteAsyncConnection Connection { get; } = connection;

        public void Dispose() => operationLock.Release();
    }
}
