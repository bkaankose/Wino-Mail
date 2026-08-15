#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using SQLite;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Contracts.Intelligence;
using Wino.Messaging.UI;

namespace Wino.Services;

public sealed class LocalIntelligenceStore(IApplicationConfiguration applicationConfiguration) : ILocalIntelligenceStore, IAsyncDisposable
{
    private const string DatabaseName = "WinoIntelligence.db";
    private static readonly string[] LegacyDatabaseNames = ["WinoSemanticIndex.db", "WinoSemantic.db"];
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private SQLiteAsyncConnection? _connection;

    public bool DatabaseExists => File.Exists(GetDatabasePath());

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
            return;

        await _initializeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
                return;

            Directory.CreateDirectory(applicationConfiguration.ApplicationDataFolderPath);
            RemoveLegacyDatabases(applicationConfiguration.ApplicationDataFolderPath);
            if (!string.Equals(applicationConfiguration.PublisherSharedFolderPath, applicationConfiguration.ApplicationDataFolderPath, StringComparison.OrdinalIgnoreCase))
                RemoveLegacyDatabases(applicationConfiguration.PublisherSharedFolderPath);
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
            await connection.CreateTableAsync<LocalIndexJobRow>().ConfigureAwait(false);
            var jobColumns = await connection.GetTableInfoAsync("LocalIndexJob").ConfigureAwait(false);
            if (jobColumns.All(x => x.Name != nameof(LocalIndexJobRow.ThroughUtcExclusive)))
            {
                await connection.ExecuteAsync(
                    $"ALTER TABLE LocalIndexJob ADD COLUMN {nameof(LocalIndexJobRow.ThroughUtcExclusive)} TEXT NULL")
                    .ConfigureAwait(false);
            }
            await connection.ExecuteAsync("DROP TABLE IF EXISTS LocalMessageKey").ConfigureAwait(false);
            await connection.CreateTableAsync<LocalPreparedDocumentRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<LocalIntelligenceAccessRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<LocalAccountIntelligenceSnapshotRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<LocalDailyBriefingStateRow>().ConfigureAwait(false);
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

    public async Task<long> GetLastImportedRevisionAsync(Guid localAccountId, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        var row = await connection.Table<LocalMailboxStateRow>()
            .Where(x => x.LocalAccountId == localAccountId)
            .FirstOrDefaultAsync().ConfigureAwait(false);
        return row?.LastImportedRevision ?? 0;
    }

    public async Task<IReadOnlySet<string>> GetCompletedMessageIdsAsync(
        Guid localAccountId,
        IReadOnlyList<IntelligenceCapabilityDto> capabilities,
        CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        var rows = await connection.Table<LocalArtifactRow>()
            .Where(x => x.LocalAccountId == localAccountId && !x.IsDeleted)
            .ToListAsync().ConfigureAwait(false);
        var required = capabilities.ToDictionary(x => x.CapabilityId, x => x.GenerationVersion, StringComparer.Ordinal);
        var headlineCapabilityId = IntelligenceCapabilityIds.BriefingHeadline;
        var headlineGeneration = required.GetValueOrDefault(headlineCapabilityId);
        required.Remove(headlineCapabilityId);
        var headlines = headlineGeneration > 0
            ? await connection.Table<LocalBriefingHeadlineRow>()
                .Where(x => x.LocalAccountId == localAccountId)
                .ToListAsync().ConfigureAwait(false)
            : [];
        return rows
            .GroupBy(x => new { x.RemoteMessageId, x.ContentHash })
            .Where(group => required.All(capability => group.Any(row =>
                row.CapabilityId == capability.Key && row.GenerationVersion >= capability.Value)) &&
                (headlineGeneration == 0 || headlines.Any(headline =>
                    headline.RemoteMessageId == group.Key.RemoteMessageId &&
                    headline.ContentHash == group.Key.ContentHash &&
                    headline.GenerationVersion >= headlineGeneration)))
            .Select(group => group.Key.RemoteMessageId)
            .ToHashSet(StringComparer.Ordinal);
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
        if (distinctIds.Length == 0 || !DatabaseExists)
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

    public async Task<IntelligenceIndexDocumentRequest?> GetPreparedDocumentAsync(
        Guid localAccountId,
        string remoteMessageId,
        CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        var key = PreparedDocumentKey(localAccountId, remoteMessageId);
        var row = await connection.Table<LocalPreparedDocumentRow>()
            .Where(x => x.Key == key)
            .FirstOrDefaultAsync().ConfigureAwait(false);
        if (row is null)
            return null;

        byte[]? plaintext = null;
        try
        {
            plaintext = Unprotect(row.ProtectedPayload, localAccountId, remoteMessageId);
            var document = JsonSerializer.Deserialize(
                plaintext,
                WinoAccountApiJsonContext.Default.IntelligenceIndexDocumentRequest);
            if (document is null || !string.Equals(document.ContentHash, row.ContentHash, StringComparison.Ordinal))
                throw new CryptographicException("The staged intelligence document failed its integrity check.");
            return document;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            await connection.ExecuteAsync("DELETE FROM LocalPreparedDocument WHERE Key = ?", key).ConfigureAwait(false);
            return null;
        }
        finally
        {
            if (plaintext is not null)
                CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task SavePreparedDocumentAsync(
        Guid localAccountId,
        string remoteMessageId,
        IntelligenceIndexDocumentRequest document,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteMessageId);
        ArgumentNullException.ThrowIfNull(document);
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            document,
            WinoAccountApiJsonContext.Default.IntelligenceIndexDocumentRequest);
        byte[]? protectedPayload = null;
        try
        {
            protectedPayload = Protect(plaintext, localAccountId, remoteMessageId);
            await connection.InsertOrReplaceAsync(new LocalPreparedDocumentRow
            {
                Key = PreparedDocumentKey(localAccountId, remoteMessageId),
                LocalAccountId = localAccountId,
                RemoteMessageId = remoteMessageId,
                ContentHash = document.ContentHash,
                ProtectedPayload = protectedPayload,
                UpdatedAtUtc = DateTime.UtcNow,
            }, typeof(LocalPreparedDocumentRow)).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedPayload is not null)
                CryptographicOperations.ZeroMemory(protectedPayload);
        }
    }

    public async Task DeletePreparedDocumentsAsync(
        Guid localAccountId,
        IReadOnlyCollection<string> remoteMessageIds,
        CancellationToken cancellationToken = default)
    {
        if (remoteMessageIds.Count == 0)
            return;

        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await connection.RunInTransactionAsync(transaction =>
        {
            foreach (var remoteMessageId in remoteMessageIds.Distinct(StringComparer.Ordinal))
            {
                transaction.Execute(
                    "DELETE FROM LocalPreparedDocument WHERE LocalAccountId = ? AND RemoteMessageId = ?",
                    localAccountId,
                    remoteMessageId);
            }
        }).ConfigureAwait(false);
    }

    public async Task ImportAsync(Guid localAccountId, Guid mailboxId, IReadOnlyList<IntelligenceArtifactDto> artifacts, long throughRevision, CancellationToken cancellationToken = default)
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
                LastImportedRevision = throughRevision,
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
            WeakReferenceMessenger.Default.Send(new IntelligenceMetadataChanged(
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
                    Key = $"{localAccountId:D}|{headline.BriefingId:D}", LocalAccountId = localAccountId, MailboxId = mailboxId,
                    RemoteMessageId = existing?.RemoteMessageId ?? string.Empty,
                    ContentHash = existing?.ContentHash ?? string.Empty,
                    GenerationVersion = existing?.GenerationVersion ?? 0,
                    BriefingId = headline.BriefingId, Headline = headline.Headline, ArtifactRevision = headline.ArtifactRevision,
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
        WeakReferenceMessenger.Default.Send(new IntelligenceMetadataChanged(localAccountId, new HashSet<string>(), IntelligenceMetadataChangeScope.Messages));
    }

    public async Task SaveAccessSnapshotAsync(LocalIntelligenceAccessSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        await lease.Connection.InsertOrReplaceAsync(new LocalIntelligenceAccessRow
        {
            LocalAccountId = snapshot.LocalAccountId, WinoAccountId = snapshot.WinoAccountId,
            HasAiPack = snapshot.HasAiPack, HasIntelligenceConsent = snapshot.HasIntelligenceConsent,
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

    public async Task DeleteMailboxAsync(Guid localAccountId, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await connection.RunInTransactionAsync(transaction =>
        {
            transaction.Execute("DELETE FROM LocalArtifact WHERE LocalAccountId = ?", localAccountId);
            transaction.Execute("DELETE FROM LocalBriefingHeadline WHERE LocalAccountId = ?", localAccountId);
            transaction.Execute("DELETE FROM LocalMailboxState WHERE LocalAccountId = ?", localAccountId);
            transaction.Execute("DELETE FROM LocalIndexJob WHERE LocalAccountId = ?", localAccountId);
            transaction.Execute("DELETE FROM LocalPreparedDocument WHERE LocalAccountId = ?", localAccountId);
            transaction.Execute("DELETE FROM LocalIntelligenceAccess WHERE LocalAccountId = ?", localAccountId);
            transaction.Execute("DELETE FROM LocalDailyBriefingState WHERE LocalAccountId = ?", localAccountId);
        }).ConfigureAwait(false);
        WeakReferenceMessenger.Default.Send(new IntelligenceMetadataChanged(
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

        WeakReferenceMessenger.Default.Send(new IntelligenceMetadataChanged(
            null,
            new HashSet<string>(StringComparer.Ordinal),
            IntelligenceMetadataChangeScope.DatabaseReset));
    }

    public async Task SaveJobIntentAsync(SemanticIndexJobIntent intent, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await connection.InsertOrReplaceAsync(new LocalIndexJobRow
        {
            LocalAccountId = intent.LocalAccountId,
            MailboxId = intent.ServerMailboxId,
            RangePresetId = intent.RangePreset.ToStableId(),
            CutoffUtc = intent.CutoffUtc?.UtcDateTime,
            ThroughUtcExclusive = intent.ThroughUtcExclusive?.UtcDateTime,
            AutomaticallyIndexNewMessages = intent.AutomaticallyIndexNewMessages,
            BackfillStatus = intent.BackfillStatus,
            UpdatedAtUtc = intent.UpdatedAtUtc.UtcDateTime,
        }, typeof(LocalIndexJobRow)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SemanticIndexJobIntent>> GetJobIntentsAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        var rows = await connection.Table<LocalIndexJobRow>().ToListAsync().ConfigureAwait(false);
        return rows.Select(row => new SemanticIndexJobIntent(
            row.LocalAccountId,
            row.MailboxId,
            SemanticIndexRangePresetExtensions.FromStableId(row.RangePresetId),
            row.CutoffUtc is null ? null : new DateTimeOffset(DateTime.SpecifyKind(row.CutoffUtc.Value, DateTimeKind.Utc)),
            row.ThroughUtcExclusive is null ? null : new DateTimeOffset(DateTime.SpecifyKind(row.ThroughUtcExclusive.Value, DateTimeKind.Utc)),
            row.AutomaticallyIndexNewMessages,
            row.BackfillStatus,
            new DateTimeOffset(DateTime.SpecifyKind(row.UpdatedAtUtc, DateTimeKind.Utc)))).ToArray();
    }

    public async Task DeleteJobIntentAsync(Guid localAccountId, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var connection = lease.Connection;
        await connection.ExecuteAsync("DELETE FROM LocalIndexJob WHERE LocalAccountId = ?", localAccountId).ConfigureAwait(false);
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
            await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
            return new ConnectionLease(_connection!, _operationLock);
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
        => DeserializeTypedArtifact(row.PayloadJson);

    internal static string SerializeStoredArtifact(IntelligenceArtifactDto artifact)
        => JsonSerializer.Serialize(artifact, WinoAccountApiJsonContext.Default.IntelligenceArtifactDto);

    internal static IntelligenceArtifactDto DeserializeTypedArtifact(string json)
        => JsonSerializer.Deserialize(json, WinoAccountApiJsonContext.Default.IntelligenceArtifactDto)
            ?? throw new JsonException("Stored intelligence artifact is empty.");

    private static void RemoveLegacyDatabases(string folder)
    {
        foreach (var databaseName in LegacyDatabaseNames)
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var path = Path.Combine(folder, databaseName + suffix);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }

    private static string PreparedDocumentKey(Guid localAccountId, string remoteMessageId)
        => $"{localAccountId:D}|{remoteMessageId}";

    private static byte[] Protect(byte[] plaintext, Guid localAccountId, string remoteMessageId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Prepared intelligence documents require Windows data protection.");
        var entropy = CreateEntropy(localAccountId, remoteMessageId);
        try { return ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(entropy); }
    }

    private static byte[] Unprotect(byte[] protectedPayload, Guid localAccountId, string remoteMessageId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Prepared intelligence documents require Windows data protection.");
        var entropy = CreateEntropy(localAccountId, remoteMessageId);
        try { return ProtectedData.Unprotect(protectedPayload, entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(entropy); }
    }

    private static byte[] CreateEntropy(Guid localAccountId, string remoteMessageId)
        => SHA256.HashData(Encoding.UTF8.GetBytes($"wino-intelligence-document-v1|{localAccountId:D}|{remoteMessageId}"));

    [Table("LocalArtifact")]
    private sealed class LocalArtifactRow
    {
        [PrimaryKey] public string Key { get; set; } = string.Empty;
        [Indexed] public Guid LocalAccountId { get; set; }
        public Guid MailboxId { get; set; }
        [Indexed] public string RemoteMessageId { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;
        [Indexed] public string CapabilityId { get; set; } = string.Empty;
        public int GenerationVersion { get; set; }
        public int PayloadSchemaVersion { get; set; }
        [Indexed] public long ArtifactRevision { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
        public double? Confidence { get; set; }
        public bool IsDeleted { get; set; }
        public string PayloadJson { get; set; } = "{}";
    }

    [Table("LocalMailboxState")]
    private sealed class LocalMailboxStateRow
    {
        [PrimaryKey] public Guid LocalAccountId { get; set; }
        public Guid MailboxId { get; set; }
        public long LastImportedRevision { get; set; }
        public string HeadlineLanguage { get; set; } = string.Empty;
        public bool SuppressHeadlineLanguagePrompt { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    [Table("LocalBriefingHeadline")]
    private sealed class LocalBriefingHeadlineRow
    {
        [PrimaryKey] public string Key { get; set; } = string.Empty;
        [Indexed] public Guid LocalAccountId { get; set; }
        public Guid MailboxId { get; set; }
        [Indexed] public string RemoteMessageId { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;
        public int GenerationVersion { get; set; }
        [Indexed] public Guid BriefingId { get; set; }
        public string Headline { get; set; } = string.Empty;
        public long ArtifactRevision { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    [Table("LocalIndexJob")]
    private sealed class LocalIndexJobRow
    {
        [PrimaryKey] public Guid LocalAccountId { get; set; }
        public Guid MailboxId { get; set; }
        public string RangePresetId { get; set; } = "only-new";
        public DateTime? CutoffUtc { get; set; }
        public DateTime? ThroughUtcExclusive { get; set; }
        public bool AutomaticallyIndexNewMessages { get; set; } = true;
        public string BackfillStatus { get; set; } = "not-started";
        public DateTime UpdatedAtUtc { get; set; }
    }

    [Table("LocalPreparedDocument")]
    private sealed class LocalPreparedDocumentRow
    {
        [PrimaryKey] public string Key { get; set; } = string.Empty;
        [Indexed] public Guid LocalAccountId { get; set; }
        [Indexed] public string RemoteMessageId { get; set; } = string.Empty;
        public string ContentHash { get; set; } = string.Empty;
        public byte[] ProtectedPayload { get; set; } = [];
        public DateTime UpdatedAtUtc { get; set; }
    }

    [Table("LocalIntelligenceAccess")]
    private sealed class LocalIntelligenceAccessRow
    {
        [PrimaryKey] public Guid LocalAccountId { get; set; }
        public Guid WinoAccountId { get; set; }
        public bool HasAiPack { get; set; }
        public bool HasIntelligenceConsent { get; set; }
        public Guid? MailboxId { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    [Table("LocalAccountIntelligenceSnapshot")]
    private sealed class LocalAccountIntelligenceSnapshotRow
    {
        [PrimaryKey] public Guid WinoAccountId { get; set; }
        public string Payload { get; set; } = string.Empty;
        public DateTime UpdatedAtUtc { get; set; }
    }

    [Table("LocalDailyBriefingState")]
    private sealed class LocalDailyBriefingStateRow
    {
        [PrimaryKey] public Guid LocalAccountId { get; set; }
        public DateTime? LastOpenedAtUtc { get; set; }
        public DateTime? LastViewedAtUtc { get; set; }
        public long LastViewedFactRevision { get; set; }
    }

    private sealed class ConnectionLease(SQLiteAsyncConnection connection, SemaphoreSlim operationLock) : IDisposable
    {
        public SQLiteAsyncConnection Connection { get; } = connection;

        public void Dispose() => operationLock.Release();
    }
}
