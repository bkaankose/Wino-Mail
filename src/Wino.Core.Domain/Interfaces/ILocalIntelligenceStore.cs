using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Core.Domain.Interfaces;

public interface ILocalIntelligenceStore
{
    bool DatabaseExists { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<long> GetLastImportedRevisionAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<string>> GetCompletedMessageIdsAsync(Guid localAccountId, IReadOnlyList<IntelligenceCapabilityDto> capabilities, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IntelligenceArtifactDto>> GetCurrentArtifactsAsync(Guid localAccountId, string remoteMessageId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, IReadOnlyList<IntelligenceArtifactDto>>> GetCurrentArtifactsAsync(Guid localAccountId, IReadOnlyCollection<string> remoteMessageIds, CancellationToken cancellationToken = default);
    Task<IntelligenceIndexDocumentRequest?> GetPreparedDocumentAsync(Guid localAccountId, string remoteMessageId, CancellationToken cancellationToken = default);
    Task SavePreparedDocumentAsync(Guid localAccountId, string remoteMessageId, IntelligenceIndexDocumentRequest document, CancellationToken cancellationToken = default);
    Task DeletePreparedDocumentsAsync(Guid localAccountId, IReadOnlyCollection<string> remoteMessageIds, CancellationToken cancellationToken = default);
    Task ImportAsync(Guid localAccountId, Guid mailboxId, IReadOnlyList<IntelligenceArtifactDto> artifacts, long throughRevision, CancellationToken cancellationToken = default);
    Task<string?> GetHeadlineLanguageAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task SetHeadlineLanguageAsync(Guid localAccountId, Guid mailboxId, string language, CancellationToken cancellationToken = default);
    Task<bool> GetHeadlineLanguagePromptSuppressedAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task SetHeadlineLanguagePromptSuppressedAsync(Guid localAccountId, bool suppressed, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, string>> GetBriefingHeadlinesAsync(Guid localAccountId, IReadOnlyCollection<Guid> briefingIds, CancellationToken cancellationToken = default);
    Task ApplyBriefingHeadlineUpdatesAsync(Guid localAccountId, Guid mailboxId, string language, IReadOnlyList<BriefingHeadlineUpdateDto> headlines, long throughRevision, CancellationToken cancellationToken = default);
    Task SaveJobIntentAsync(SemanticIndexJobIntent intent, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SemanticIndexJobIntent>> GetJobIntentsAsync(CancellationToken cancellationToken = default);
    Task DeleteJobIntentAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task SaveAccessSnapshotAsync(LocalIntelligenceAccessSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<LocalIntelligenceAccessSnapshot?> GetAccessSnapshotAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task DeleteAccessSnapshotsAsync(CancellationToken cancellationToken = default);
    Task<long> GetLatestBriefingFactRevisionAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task<DailyBriefingUnseenState> GetDailyBriefingUnseenStateAsync(IReadOnlyCollection<Guid> localAccountIds, CancellationToken cancellationToken = default);
    Task MarkDailyBriefingOpenedAsync(IReadOnlyCollection<Guid> localAccountIds, DateTimeOffset openedAtUtc, CancellationToken cancellationToken = default);
    Task MarkDailyBriefingViewedAsync(IReadOnlyCollection<Guid> localAccountIds, DateTimeOffset viewedAtUtc, CancellationToken cancellationToken = default);
    Task DeleteMailboxAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task DeleteDatabaseAsync(CancellationToken cancellationToken = default);
}
