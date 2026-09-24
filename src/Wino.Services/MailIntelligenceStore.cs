#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using Wino.Core.Domain.Entities.Intelligence;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Services;

/// <summary>
/// Device-local intelligence database. Separate from the mail database so the cutover is
/// a file delete rather than a migration against live mail.
/// </summary>
public sealed class MailIntelligenceStore(
    IApplicationConfiguration applicationConfiguration) : IMailIntelligenceStore, IAsyncDisposable
{
    private const string DatabaseName = "WinoMailIntelligence.db";

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private SQLiteAsyncConnection? _connection;
    private bool _disposed;

    public bool DatabaseExists => File.Exists(GetDatabasePath());

    public async Task InitializeAsync()
    {
        using var lease = await GetConnectionLeaseAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private string GetDatabasePath()
        => Path.Combine(applicationConfiguration.ApplicationDataFolderPath, DatabaseName);

    private async Task<SQLiteAsyncConnection> InitializeConnectionAsync()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        SQLiteAsyncConnection? connection = null;
        try
        {
            Directory.CreateDirectory(applicationConfiguration.ApplicationDataFolderPath);
            connection = new SQLiteAsyncConnection(
                GetDatabasePath(),
                SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex);

            await connection.CreateTableAsync<ClassificationArtifactRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<EnrichmentArtifactRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<MailIntelligenceJobRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<BriefingIgnoreRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<BriefingViewStateRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<MailIntelligenceAccessRow>().ConfigureAwait(false);
            await connection.CreateTableAsync<AccountIntelligenceSnapshotRow>().ConfigureAwait(false);

            _connection = connection;
            return connection;
        }
        catch
        {
            if (connection is not null)
            {
                try
                {
                    await connection.CloseAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the initialization failure if closing also fails.
                }
            }

            throw;
        }
    }

    // ---- jobs ----------------------------------------------------------------------

    public async Task UpsertJobAsync(MailIntelligenceJobState job, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        await lease.Connection.InsertOrReplaceAsync(new MailIntelligenceJobRow
        {
            JobId = job.JobId,
            LocalAccountId = job.LocalAccountId,
            MailboxId = job.MailboxId,
            MessageCount = job.MessageCount,
            Status = job.Status,
            ClassificationStatus = job.Classification.Status,
            ClassificationPageCount = job.Classification.PageCount,
            ClassificationDigest = job.Classification.Digest,
            IsClassificationImported = job.Classification.IsImported,
            IsClassificationAcknowledged = job.Classification.IsAcknowledged,
            EnrichmentStatus = job.Enrichment.Status,
            EnrichmentPageCount = job.Enrichment.PageCount,
            EnrichmentDigest = job.Enrichment.Digest,
            IsEnrichmentImported = job.Enrichment.IsImported,
            IsEnrichmentAcknowledged = job.Enrichment.IsAcknowledged,
            FailedCount = job.FailedCount,
            LastError = job.LastError,
            CreatedUtc = job.CreatedUtc,
            UpdatedUtc = DateTime.UtcNow,
        }, typeof(MailIntelligenceJobRow)).ConfigureAwait(false);
    }

    public async Task<MailIntelligenceJobState?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var row = await lease.Connection.Table<MailIntelligenceJobRow>()
            .Where(x => x.JobId == jobId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<MailIntelligenceJobState>> GetUnfinishedJobsAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var rows = await lease.Connection.Table<MailIntelligenceJobRow>()
            .Where(x => !x.IsClassificationAcknowledged || !x.IsEnrichmentAcknowledged)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync()
            .ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    public async Task<IReadOnlyList<MailIntelligenceJobState>> GetJobsForAccountAsync(
        Guid localAccountId, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var rows = await lease.Connection.Table<MailIntelligenceJobRow>()
            .Where(x => x.LocalAccountId == localAccountId)
            .OrderBy(x => x.CreatedUtc)
            .ToListAsync()
            .ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    public async Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        await lease.Connection.DeleteAsync<MailIntelligenceJobRow>(jobId).ConfigureAwait(false);
    }

    // ---- imports -------------------------------------------------------------------

    public async Task<MailIntelligenceImportResult> ImportClassificationPageAsync(
        Guid localAccountId,
        IReadOnlyList<ClassificationArtifact> artifacts,
        IReadOnlyList<MailIntelligenceItemFailure> failures,
        IReadOnlyDictionary<string, string> desiredHashes,
        CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);

        var fresh = new List<ClassificationArtifact>();
        var stale = 0;
        foreach (var artifact in artifacts)
        {
            if (IsStale(artifact.Key, desiredHashes))
            {
                stale++;
                continue;
            }

            fresh.Add(artifact);
        }

        var now = DateTime.UtcNow;
        var existing = await LoadFirstImportedAsync<ClassificationArtifactRow>(
            lease.Connection, localAccountId, fresh.Select(x => x.Key.RemoteMessageId)).ConfigureAwait(false);

        // One transaction per page. The stage is only acknowledged after this commits.
        await lease.Connection.RunInTransactionAsync(connection =>
        {
            foreach (var artifact in fresh)
            {
                var key = ClassificationArtifactRow.BuildKey(localAccountId, artifact.Key.RemoteMessageId);
                connection.InsertOrReplace(new ClassificationArtifactRow
                {
                    Key = key,
                    LocalAccountId = localAccountId,
                    RemoteMessageId = artifact.Key.RemoteMessageId,
                    ContentHash = artifact.Key.ContentHash,
                    Labels = string.Join(',', artifact.Labels),
                    Priority = artifact.Priority,
                    Action = artifact.Action,
                    IncludeInBriefing = artifact.IncludeInBriefing,
                    SignalsJson = SerializeSignals(artifact.Signals),
                    CompletedUtc = artifact.CompletedUtc,
                    // Re-importing the same identity keeps the original arrival time, so a
                    // duplicate result never makes an old card look new.
                    FirstImportedUtc = existing.TryGetValue(artifact.Key.RemoteMessageId, out var first) ? first : now,
                }, typeof(ClassificationArtifactRow));
            }
        }).ConfigureAwait(false);

        return new MailIntelligenceImportResult(fresh.Count, stale, failures.Count);
    }

    public async Task<MailIntelligenceImportResult> ImportEnrichmentPageAsync(
        Guid localAccountId,
        IReadOnlyList<EnrichmentArtifact> artifacts,
        IReadOnlyList<MailIntelligenceItemFailure> failures,
        IReadOnlyDictionary<string, string> desiredHashes,
        CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);

        var fresh = new List<EnrichmentArtifact>();
        var stale = 0;
        foreach (var artifact in artifacts)
        {
            if (IsStale(artifact.Key, desiredHashes))
            {
                stale++;
                continue;
            }

            fresh.Add(artifact);
        }

        var now = DateTime.UtcNow;
        var existing = await LoadFirstImportedAsync<EnrichmentArtifactRow>(
            lease.Connection, localAccountId, fresh.Select(x => x.Key.RemoteMessageId)).ConfigureAwait(false);

        await lease.Connection.RunInTransactionAsync(connection =>
        {
            foreach (var artifact in fresh)
            {
                connection.InsertOrReplace(new EnrichmentArtifactRow
                {
                    Key = EnrichmentArtifactRow.BuildKey(localAccountId, artifact.Key.RemoteMessageId),
                    LocalAccountId = localAccountId,
                    RemoteMessageId = artifact.Key.RemoteMessageId,
                    ContentHash = artifact.Key.ContentHash,
                    Headline = artifact.Headline,
                    Summary = artifact.Summary,
                    CompletedUtc = artifact.CompletedUtc,
                    FirstImportedUtc = existing.TryGetValue(artifact.Key.RemoteMessageId, out var first) ? first : now,
                }, typeof(EnrichmentArtifactRow));
            }
        }).ConfigureAwait(false);

        return new MailIntelligenceImportResult(fresh.Count, stale, failures.Count);
    }

    /// <summary>
    /// An artifact is stale when the caller knows a different hash for that message, which
    /// means the content changed after the job was submitted.
    /// </summary>
    private static bool IsStale(MailArtifactKey key, IReadOnlyDictionary<string, string> desiredHashes)
        => desiredHashes.Count > 0 &&
           desiredHashes.TryGetValue(key.RemoteMessageId, out var desired) &&
           !string.Equals(desired, key.ContentHash, StringComparison.OrdinalIgnoreCase);

    private static async Task<Dictionary<string, DateTime>> LoadFirstImportedAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TRow>(
        SQLiteAsyncConnection connection,
        Guid localAccountId,
        IEnumerable<string> remoteMessageIds)
        where TRow : new()
    {
        var ids = remoteMessageIds.Distinct(StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        if (ids.Length == 0)
        {
            return result;
        }

        var table = typeof(TRow) == typeof(ClassificationArtifactRow) ? "ClassificationArtifact" : "EnrichmentArtifact";
        foreach (var chunk in ids.Chunk(400))
        {
            var parameters = new object[chunk.Length + 1];
            parameters[0] = localAccountId;
            for (var index = 0; index < chunk.Length; index++)
            {
                parameters[index + 1] = chunk[index];
            }

            var placeholders = string.Join(",", Enumerable.Repeat("?", chunk.Length));
            var rows = await connection.QueryAsync<FirstImportedProjection>(
                $"SELECT RemoteMessageId, FirstImportedUtc FROM {table} WHERE LocalAccountId = ? AND RemoteMessageId IN ({placeholders})",
                parameters).ConfigureAwait(false);

            foreach (var row in rows)
            {
                result[row.RemoteMessageId] = row.FirstImportedUtc;
            }
        }

        return result;
    }

    private sealed class FirstImportedProjection
    {
        public string RemoteMessageId { get; set; } = string.Empty;
        public DateTime FirstImportedUtc { get; set; }
    }

    public async Task MarkStageImportedAsync(Guid jobId, MailIntelligenceStageKind stage, CancellationToken cancellationToken = default)
        => await UpdateJobRowAsync(jobId, row =>
        {
            if (stage == MailIntelligenceStageKind.Classification)
            {
                row.IsClassificationImported = true;
            }
            else
            {
                row.IsEnrichmentImported = true;
            }
        }, cancellationToken).ConfigureAwait(false);

    public async Task MarkStageAcknowledgedAsync(Guid jobId, MailIntelligenceStageKind stage, CancellationToken cancellationToken = default)
        => await UpdateJobRowAsync(jobId, row =>
        {
            if (stage == MailIntelligenceStageKind.Classification)
            {
                row.IsClassificationAcknowledged = true;
            }
            else
            {
                row.IsEnrichmentAcknowledged = true;
            }
        }, cancellationToken).ConfigureAwait(false);

    private async Task UpdateJobRowAsync(Guid jobId, Action<MailIntelligenceJobRow> mutate, CancellationToken cancellationToken)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var row = await lease.Connection.Table<MailIntelligenceJobRow>()
            .Where(x => x.JobId == jobId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        if (row is null)
        {
            return;
        }

        mutate(row);
        row.UpdatedUtc = DateTime.UtcNow;
        await lease.Connection.InsertOrReplaceAsync(row, typeof(MailIntelligenceJobRow)).ConfigureAwait(false);
    }

    // ---- artifacts -----------------------------------------------------------------

    public async Task<IReadOnlyDictionary<string, ClassificationArtifact>> GetClassificationArtifactsAsync(
        Guid localAccountId, IReadOnlyCollection<string> remoteMessageIds, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, ClassificationArtifact>(StringComparer.Ordinal);
        foreach (var chunk in remoteMessageIds.Distinct(StringComparer.Ordinal).Chunk(400))
        {
            var rows = await QueryByIdsAsync<ClassificationArtifactRow>(lease.Connection, "ClassificationArtifact", localAccountId, chunk).ConfigureAwait(false);
            foreach (var row in rows)
            {
                result[row.RemoteMessageId] = Map(row);
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, EnrichmentArtifact>> GetEnrichmentArtifactsAsync(
        Guid localAccountId, IReadOnlyCollection<string> remoteMessageIds, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, EnrichmentArtifact>(StringComparer.Ordinal);
        foreach (var chunk in remoteMessageIds.Distinct(StringComparer.Ordinal).Chunk(400))
        {
            var rows = await QueryByIdsAsync<EnrichmentArtifactRow>(lease.Connection, "EnrichmentArtifact", localAccountId, chunk).ConfigureAwait(false);
            foreach (var row in rows)
            {
                result[row.RemoteMessageId] = Map(row);
            }
        }

        return result;
    }

    public async Task<IReadOnlySet<string>> GetProcessedMessageIdsAsync(
        Guid localAccountId, IReadOnlyCollection<string> remoteMessageIds, CancellationToken cancellationToken = default)
    {
        var artifacts = await GetClassificationArtifactsAsync(localAccountId, remoteMessageIds, cancellationToken).ConfigureAwait(false);
        return artifacts.Keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<ClassificationArtifact>> GetBriefingCandidatesAsync(
        Guid localAccountId, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var rows = await lease.Connection.Table<ClassificationArtifactRow>()
            .Where(x => x.LocalAccountId == localAccountId && x.IncludeInBriefing)
            .ToListAsync()
            .ConfigureAwait(false);
        return [.. rows.Select(Map)];
    }

    public async Task<DateTime?> GetFirstImportedUtcAsync(
        Guid localAccountId, string remoteMessageId, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var row = await lease.Connection.Table<ClassificationArtifactRow>()
            .Where(x => x.LocalAccountId == localAccountId && x.RemoteMessageId == remoteMessageId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        return row?.FirstImportedUtc;
    }

    private static async Task<List<TRow>> QueryByIdsAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TRow>(
        SQLiteAsyncConnection connection, string table, Guid localAccountId, string[] ids)
        where TRow : new()
    {
        var parameters = new object[ids.Length + 1];
        parameters[0] = localAccountId;
        for (var index = 0; index < ids.Length; index++)
        {
            parameters[index + 1] = ids[index];
        }

        var placeholders = string.Join(",", Enumerable.Repeat("?", ids.Length));
        return await connection.QueryAsync<TRow>(
            $"SELECT * FROM {table} WHERE LocalAccountId = ? AND RemoteMessageId IN ({placeholders})",
            parameters).ConfigureAwait(false);
    }

    // ---- briefing state ------------------------------------------------------------

    public async Task SetIgnoredAsync(
        Guid localAccountId, string remoteMessageId, string contentHash, bool isIgnored, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var key = BriefingIgnoreRow.BuildKey(localAccountId, remoteMessageId);
        if (!isIgnored)
        {
            await lease.Connection.DeleteAsync<BriefingIgnoreRow>(key).ConfigureAwait(false);
            return;
        }

        await lease.Connection.InsertOrReplaceAsync(new BriefingIgnoreRow
        {
            Key = key,
            LocalAccountId = localAccountId,
            RemoteMessageId = remoteMessageId,
            ContentHash = contentHash,
            IgnoredUtc = DateTime.UtcNow,
        }, typeof(BriefingIgnoreRow)).ConfigureAwait(false);
    }

    /// <summary>Ignored messages, mapped to the content hash that was ignored.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetIgnoredAsync(
        Guid localAccountId, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var rows = await lease.Connection.Table<BriefingIgnoreRow>()
            .Where(x => x.LocalAccountId == localAccountId)
            .ToListAsync()
            .ConfigureAwait(false);
        return rows.ToDictionary(x => x.RemoteMessageId, x => x.ContentHash, StringComparer.Ordinal);
    }

    public async Task<(DateTime? LastOpenedUtc, DateTime? LastViewedUtc)> GetBriefingViewStateAsync(
        Guid localAccountId, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var row = await lease.Connection.Table<BriefingViewStateRow>()
            .Where(x => x.LocalAccountId == localAccountId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        return (row?.LastOpenedUtc, row?.LastViewedUtc);
    }

    public async Task MarkBriefingViewedAsync(Guid localAccountId, CancellationToken cancellationToken = default)
        => await UpdateViewStateAsync(localAccountId, row => row.LastViewedUtc = DateTime.UtcNow, cancellationToken).ConfigureAwait(false);

    public async Task MarkBriefingOpenedAsync(Guid localAccountId, CancellationToken cancellationToken = default)
        => await UpdateViewStateAsync(localAccountId, row => row.LastOpenedUtc = DateTime.UtcNow, cancellationToken).ConfigureAwait(false);

    private async Task UpdateViewStateAsync(Guid localAccountId, Action<BriefingViewStateRow> mutate, CancellationToken cancellationToken)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var row = await lease.Connection.Table<BriefingViewStateRow>()
            .Where(x => x.LocalAccountId == localAccountId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false) ?? new BriefingViewStateRow { LocalAccountId = localAccountId };
        mutate(row);
        await lease.Connection.InsertOrReplaceAsync(row, typeof(BriefingViewStateRow)).ConfigureAwait(false);
    }

    // ---- access --------------------------------------------------------------------

    public async Task SaveAccessAsync(
        Guid localAccountId, Guid winoAccountId, Guid mailboxId, bool hasAiPack, bool hasConsent, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        await lease.Connection.InsertOrReplaceAsync(new MailIntelligenceAccessRow
        {
            LocalAccountId = localAccountId,
            WinoAccountId = winoAccountId,
            MailboxId = mailboxId,
            HasAiPack = hasAiPack,
            HasIntelligenceConsent = hasConsent,
            UpdatedUtc = DateTime.UtcNow,
        }, typeof(MailIntelligenceAccessRow)).ConfigureAwait(false);
    }

    public async Task<(Guid MailboxId, bool HasAiPack, bool HasConsent)?> GetAccessAsync(
        Guid localAccountId, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var row = await lease.Connection.Table<MailIntelligenceAccessRow>()
            .Where(x => x.LocalAccountId == localAccountId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        return row is null ? null : (row.MailboxId, row.HasAiPack, row.HasIntelligenceConsent);
    }

    // ---- cached account snapshot ---------------------------------------------------

    public async Task<string?> GetAccountSnapshotJsonAsync(Guid winoAccountId, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        var row = await lease.Connection.Table<AccountIntelligenceSnapshotRow>()
            .Where(x => x.WinoAccountId == winoAccountId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
        return row?.Payload;
    }

    public async Task SaveAccountSnapshotJsonAsync(Guid winoAccountId, string payload, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        await lease.Connection.InsertOrReplaceAsync(new AccountIntelligenceSnapshotRow
        {
            WinoAccountId = winoAccountId,
            Payload = payload,
            UpdatedUtc = DateTime.UtcNow,
        }, typeof(AccountIntelligenceSnapshotRow)).ConfigureAwait(false);
    }

    public async Task DeleteAccountSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        await lease.Connection.ExecuteAsync("DELETE FROM AccountIntelligenceSnapshot").ConfigureAwait(false);
    }

    // ---- lifecycle -----------------------------------------------------------------

    public async Task DeleteAccountAsync(Guid localAccountId, CancellationToken cancellationToken = default)
    {
        using var lease = await GetConnectionLeaseAsync(cancellationToken).ConfigureAwait(false);
        await lease.Connection.RunInTransactionAsync(connection =>
        {
            connection.Execute("DELETE FROM ClassificationArtifact WHERE LocalAccountId = ?", localAccountId);
            connection.Execute("DELETE FROM EnrichmentArtifact WHERE LocalAccountId = ?", localAccountId);
            connection.Execute("DELETE FROM MailIntelligenceJob WHERE LocalAccountId = ?", localAccountId);
            connection.Execute("DELETE FROM BriefingIgnore WHERE LocalAccountId = ?", localAccountId);
            connection.Execute("DELETE FROM BriefingViewState WHERE LocalAccountId = ?", localAccountId);
            connection.Execute("DELETE FROM MailIntelligenceAccess WHERE LocalAccountId = ?", localAccountId);
        }).ConfigureAwait(false);
    }

    public async Task DeleteDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                await _connection.CloseAsync().ConfigureAwait(false);
                _connection = null;
            }

            var path = GetDatabasePath();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                var file = path + suffix;
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_connection is not null)
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            _connection = null;
        }

        _operationLock.Dispose();
    }

    // ---- mapping -------------------------------------------------------------------

    private static MailIntelligenceJobState Map(MailIntelligenceJobRow row) => new(
        row.JobId,
        row.LocalAccountId,
        row.MailboxId,
        row.MessageCount,
        row.Status,
        new MailIntelligenceStageState(row.ClassificationStatus, row.ClassificationPageCount, row.ClassificationDigest, row.IsClassificationImported, row.IsClassificationAcknowledged),
        new MailIntelligenceStageState(row.EnrichmentStatus, row.EnrichmentPageCount, row.EnrichmentDigest, row.IsEnrichmentImported, row.IsEnrichmentAcknowledged),
        row.FailedCount,
        row.LastError,
        row.CreatedUtc,
        row.UpdatedUtc);

    private static ClassificationArtifact Map(ClassificationArtifactRow row) => new(
        new MailArtifactKey(row.RemoteMessageId, row.ContentHash),
        string.IsNullOrEmpty(row.Labels) ? [] : row.Labels.Split(',', StringSplitOptions.RemoveEmptyEntries),
        row.Priority,
        row.Action,
        row.IncludeInBriefing,
        row.CompletedUtc,
        DeserializeSignals(row.SignalsJson));

    private static string SerializeSignals(ClassificationSignals signals)
        => JsonSerializer.Serialize(signals, MailIntelligenceSignalsJsonContext.Default.ClassificationSignals);

    /// <summary>
    /// Reads the stored signals back. A row written before signals existed, or one whose
    /// payload cannot be read, falls back to empty rather than failing the import: the
    /// decision itself is in its own columns and stands on its own.
    /// </summary>
    private static ClassificationSignals DeserializeSignals(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return ClassificationSignals.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize(json, MailIntelligenceSignalsJsonContext.Default.ClassificationSignals)
                ?? ClassificationSignals.Empty;
        }
        catch (JsonException)
        {
            return ClassificationSignals.Empty;
        }
    }

    private static EnrichmentArtifact Map(EnrichmentArtifactRow row) => new(
        new MailArtifactKey(row.RemoteMessageId, row.ContentHash),
        row.Headline,
        row.Summary,
        row.CompletedUtc);

    private async Task<ConnectionLease> GetConnectionLeaseAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var connection = await InitializeConnectionAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ConnectionLease(connection, _operationLock);
        }
        catch
        {
            _operationLock.Release();
            throw;
        }
    }

    private sealed class ConnectionLease(SQLiteAsyncConnection connection, SemaphoreSlim operationLock) : IDisposable
    {
        public SQLiteAsyncConnection Connection { get; } = connection;

        public void Dispose() => operationLock.Release();
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ClassificationSignals))]
internal sealed partial class MailIntelligenceSignalsJsonContext : JsonSerializerContext;
