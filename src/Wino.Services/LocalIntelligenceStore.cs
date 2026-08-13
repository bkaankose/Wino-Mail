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
using SQLite;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Services;

public sealed class LocalIntelligenceStore(IApplicationConfiguration applicationConfiguration) : ILocalIntelligenceStore, IAsyncDisposable
{
    private const string DatabaseName = "WinoIntelligence.db";
    private static readonly string[] LegacyDatabaseNames = ["WinoSemanticIndex.db", "WinoSemantic.db"];
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private SQLiteAsyncConnection? _connection;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
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
            _connection = connection;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<long> GetLastImportedRevisionAsync(Guid localAccountId, CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
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
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.Table<LocalArtifactRow>()
            .Where(x => x.LocalAccountId == localAccountId && !x.IsDeleted)
            .ToListAsync().ConfigureAwait(false);
        var required = capabilities.ToDictionary(x => x.CapabilityId, x => x.GenerationVersion, StringComparer.Ordinal);
        return rows
            .GroupBy(x => new { x.RemoteMessageId, x.ContentHash })
            .Where(group => required.All(capability => group.Any(row =>
                row.CapabilityId == capability.Key && row.GenerationVersion >= capability.Value)))
            .Select(group => group.Key.RemoteMessageId)
            .ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<IntelligenceArtifactDto>> GetCurrentArtifactsAsync(
        Guid localAccountId,
        string remoteMessageId,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.Table<LocalArtifactRow>()
            .Where(x => x.LocalAccountId == localAccountId && x.RemoteMessageId == remoteMessageId)
            .ToListAsync().ConfigureAwait(false);
        return rows.GroupBy(x => x.CapabilityId, StringComparer.Ordinal)
            .Select(group => group.MaxBy(x => x.ArtifactRevision)!)
            .Select(DeserializeStoredArtifact)
            .ToArray();
    }

    public async Task<IntelligenceIndexDocumentRequest?> GetPreparedDocumentAsync(
        Guid localAccountId,
        string remoteMessageId,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
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
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
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

        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
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
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.RunInTransactionAsync(transaction =>
        {
            foreach (var artifact in artifacts)
            {
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
                UpdatedAtUtc = DateTime.UtcNow,
            }, typeof(LocalMailboxStateRow));
        }).ConfigureAwait(false);
        await CompactHistoryAsync(connection).ConfigureAwait(false);
    }

    public async Task DeleteMailboxAsync(Guid localAccountId, CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM LocalArtifact WHERE LocalAccountId = ?", localAccountId).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM LocalMailboxState WHERE LocalAccountId = ?", localAccountId).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM LocalIndexJob WHERE LocalAccountId = ?", localAccountId).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM LocalPreparedDocument WHERE LocalAccountId = ?", localAccountId).ConfigureAwait(false);
    }

    public async Task SaveJobIntentAsync(SemanticIndexJobIntent intent, CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
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
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
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
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync("DELETE FROM LocalIndexJob WHERE LocalAccountId = ?", localAccountId).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.CloseAsync().ConfigureAwait(false);
        _initializeLock.Dispose();
    }

    private async Task<SQLiteAsyncConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return _connection!;
    }

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
        using var document = JsonDocument.Parse(row.PayloadJson);
        if (document.RootElement.TryGetProperty("RemoteMessageId", out _) ||
            document.RootElement.TryGetProperty("remoteMessageId", out _))
        {
            return DeserializeTypedArtifact(row.PayloadJson);
        }

        if (!IntelligenceCapabilityIds.TryParse(row.CapabilityId, out var capability))
            throw new JsonException($"Stored capability '{row.CapabilityId}' is not registered.");
        var stored = new IntelligenceArtifact
        {
            RemoteMessageId = row.RemoteMessageId,
            ContentHash = row.ContentHash,
            CapabilityId = row.CapabilityId,
            GenerationVersion = row.GenerationVersion,
            PayloadSchemaVersion = row.PayloadSchemaVersion,
            ArtifactRevision = row.ArtifactRevision,
            GeneratedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(row.GeneratedAtUtc, DateTimeKind.Utc)),
            Confidence = row.Confidence,
            IsDeleted = row.IsDeleted,
            Payload = document.RootElement.Clone(),
        };
        return new IntelligenceArtifactDto
        {
            RemoteMessageId = stored.RemoteMessageId,
            ContentHash = stored.ContentHash,
            Capability = capability,
            GenerationVersion = stored.GenerationVersion,
            PayloadSchemaVersion = stored.PayloadSchemaVersion,
            ArtifactRevision = stored.ArtifactRevision,
            GeneratedAtUtc = stored.GeneratedAtUtc,
            Confidence = stored.Confidence,
            IsDeleted = stored.IsDeleted,
            SmartLabels = Read<SmartLabelsCapabilityPayload>(stored, IntelligenceCapability.SmartLabels),
            Priority = Read<PriorityCapabilityPayload>(stored, IntelligenceCapability.Priority),
            NeedsReply = Read<NeedsReplyCapabilityPayload>(stored, IntelligenceCapability.NeedsReply),
            SimilarMessages = Read<SimilarMessagesCapabilityPayload>(stored, IntelligenceCapability.SimilarMessages),
            Deadline = Read<DeadlineCapabilityPayload>(stored, IntelligenceCapability.Deadline),
            BriefingFact = Read<BriefingFactCapabilityPayload>(stored, IntelligenceCapability.BriefingFact),
            SuggestedReplies = Read<SuggestedRepliesCapabilityPayload>(stored, IntelligenceCapability.SuggestedReplies),
        };
    }

    internal static string SerializeStoredArtifact(IntelligenceArtifactDto artifact)
        => JsonSerializer.Serialize(artifact, WinoAccountApiJsonContext.Default.IntelligenceArtifactDto);

    internal static IntelligenceArtifactDto DeserializeTypedArtifact(string json)
        => JsonSerializer.Deserialize(json, WinoAccountApiJsonContext.Default.IntelligenceArtifactDto)
            ?? throw new JsonException("Stored intelligence artifact is empty.");

    private static TPayload? Read<TPayload>(IntelligenceArtifact artifact, IntelligenceCapability capability)
        where TPayload : class, IIntelligenceCapabilityPayload
        => !artifact.IsDeleted && artifact.CapabilityId == IntelligenceCapabilityIds.GetStorageId(capability)
            ? IntelligenceCapabilityEngine.DeserializePayload<TPayload>(artifact)
            : null;

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
}
