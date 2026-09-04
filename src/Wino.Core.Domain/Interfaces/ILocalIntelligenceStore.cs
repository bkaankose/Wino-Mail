using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Core.Domain.Interfaces;

public interface ILocalIntelligenceStore : IInitializeAsync
{
    bool DatabaseExists { get; }
    Task<LocalIntelligenceMailboxState?> GetMailboxStateAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task AlignMailboxHeadAsync(Guid localAccountId, MailboxIntelligenceHeadDto head, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MessageIntelligenceDownloadDto>> GetCurrentDocumentsAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalIntelligenceSearchDocument>> GetSearchDocumentsAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, MessageIntelligenceDownloadDto>> GetCurrentDocumentsAsync(Guid localAccountId, IReadOnlyCollection<string> serverMessageKeys, CancellationToken cancellationToken = default);
    Task<LocalIntelligenceChangeApplyResult> ApplyChangesAsync(Guid localAccountId, Guid mailboxId, IntelligenceChangesPageDto page, CancellationToken cancellationToken = default);
    Task<string?> GetHeadlineLanguageAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task SetHeadlineLanguageAsync(Guid localAccountId, Guid mailboxId, string language, CancellationToken cancellationToken = default);
    Task<bool> GetHeadlineLanguagePromptSuppressedAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task SetHeadlineLanguagePromptSuppressedAsync(Guid localAccountId, bool suppressed, CancellationToken cancellationToken = default);
    Task SaveAccessSnapshotAsync(LocalIntelligenceAccessSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<LocalIntelligenceAccessSnapshot?> GetAccessSnapshotAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task DeleteAccessSnapshotsAsync(CancellationToken cancellationToken = default);
    Task SaveAccountIntelligenceSnapshotAsync(WinoAccountIntelligenceSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<WinoAccountIntelligenceSnapshot?> GetAccountIntelligenceSnapshotAsync(Guid winoAccountId, CancellationToken cancellationToken = default);
    Task DeleteAccountIntelligenceSnapshotsAsync(CancellationToken cancellationToken = default);
    Task<long> GetLatestBriefingFactRevisionAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, long>> GetDailyBriefingIgnoreRevisionsAsync(Guid localAccountId,
        CancellationToken cancellationToken = default);
    Task SaveDailyBriefingIgnoreAsync(Guid localAccountId, Guid briefingId, long artifactRevision,
        DateTimeOffset ignoredAtUtc, CancellationToken cancellationToken = default);
    Task DeleteDailyBriefingIgnoreAsync(Guid localAccountId, Guid briefingId,
        CancellationToken cancellationToken = default);
    Task DeleteDailyBriefingItemAsync(Guid localAccountId, string remoteMessageId,
        CancellationToken cancellationToken = default);
    Task<DailyBriefingUnseenState> GetDailyBriefingUnseenStateAsync(IReadOnlyCollection<Guid> localAccountIds, CancellationToken cancellationToken = default);
    Task MarkDailyBriefingOpenedAsync(IReadOnlyCollection<Guid> localAccountIds, DateTimeOffset openedAtUtc, CancellationToken cancellationToken = default);
    Task MarkDailyBriefingViewedAsync(IReadOnlyCollection<Guid> localAccountIds, DateTimeOffset viewedAtUtc, CancellationToken cancellationToken = default);
    Task DeleteMailboxAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task DeleteDatabaseAsync(CancellationToken cancellationToken = default);
}
