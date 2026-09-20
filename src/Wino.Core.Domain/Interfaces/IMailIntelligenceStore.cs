#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Device-local intelligence storage. Holds Jev and Luna artifacts keyed by account,
/// remote message id and content hash, plus the jobs this device is waiting on.
/// It never stores mail bodies or embeddings.
/// </summary>
public interface IMailIntelligenceStore : IInitializeAsync
{
    bool DatabaseExists { get; }

    // Jobs.
    Task UpsertJobAsync(MailIntelligenceJobState job, CancellationToken cancellationToken = default);
    Task<MailIntelligenceJobState?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailIntelligenceJobState>> GetUnfinishedJobsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailIntelligenceJobState>> GetJobsForAccountAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports one Jev page in a single transaction. An artifact whose hash does not match
    /// the caller's desired hash for that message is skipped rather than stored.
    /// </summary>
    Task<MailIntelligenceImportResult> ImportJevPageAsync(
        Guid localAccountId,
        IReadOnlyList<JevArtifact> artifacts,
        IReadOnlyList<MailIntelligenceItemFailure> failures,
        IReadOnlyDictionary<string, string> desiredHashes,
        CancellationToken cancellationToken = default);

    /// <summary>Imports one Luna page in its own transaction, separate from Jev.</summary>
    Task<MailIntelligenceImportResult> ImportLunaPageAsync(
        Guid localAccountId,
        IReadOnlyList<LunaArtifact> artifacts,
        IReadOnlyList<MailIntelligenceItemFailure> failures,
        IReadOnlyDictionary<string, string> desiredHashes,
        CancellationToken cancellationToken = default);

    Task MarkStageImportedAsync(Guid jobId, MailIntelligenceStageKind stage, CancellationToken cancellationToken = default);
    Task MarkStageAcknowledgedAsync(Guid jobId, MailIntelligenceStageKind stage, CancellationToken cancellationToken = default);

    // Artifacts.
    Task<IReadOnlyDictionary<string, JevArtifact>> GetJevArtifactsAsync(
        Guid localAccountId, IReadOnlyCollection<string> remoteMessageIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, LunaArtifact>> GetLunaArtifactsAsync(
        Guid localAccountId, IReadOnlyCollection<string> remoteMessageIds, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetProcessedMessageIdsAsync(
        Guid localAccountId, IReadOnlyCollection<string> remoteMessageIds, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JevArtifact>> GetBriefingCandidatesAsync(Guid localAccountId, CancellationToken cancellationToken = default);

    Task<DateTime?> GetFirstImportedUtcAsync(Guid localAccountId, string remoteMessageId, CancellationToken cancellationToken = default);

    // Briefing state.
    Task SetIgnoredAsync(Guid localAccountId, string remoteMessageId, string contentHash, bool isIgnored, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, string>> GetIgnoredAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task<(DateTime? LastOpenedUtc, DateTime? LastViewedUtc)> GetBriefingViewStateAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task MarkBriefingViewedAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task MarkBriefingOpenedAsync(Guid localAccountId, CancellationToken cancellationToken = default);

    // Access.
    Task SaveAccessAsync(Guid localAccountId, Guid winoAccountId, Guid mailboxId, bool hasAiPack, bool hasConsent, CancellationToken cancellationToken = default);
    Task<(Guid MailboxId, bool HasAiPack, bool HasConsent)?> GetAccessAsync(Guid localAccountId, CancellationToken cancellationToken = default);

    // Cached account snapshot.
    Task<string?> GetAccountSnapshotJsonAsync(Guid winoAccountId, CancellationToken cancellationToken = default);
    Task SaveAccountSnapshotJsonAsync(Guid winoAccountId, string payload, CancellationToken cancellationToken = default);
    Task DeleteAccountSnapshotsAsync(CancellationToken cancellationToken = default);

    // Lifecycle.
    Task DeleteAccountAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task DeleteDatabaseAsync(CancellationToken cancellationToken = default);
}
