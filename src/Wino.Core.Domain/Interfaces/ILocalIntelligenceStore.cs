using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Core.Domain.Interfaces;

public interface ILocalIntelligenceStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<long> GetLastImportedRevisionAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task<IReadOnlySet<string>> GetCompletedMessageIdsAsync(Guid localAccountId, IReadOnlyList<IntelligenceCapabilityDto> capabilities, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IntelligenceArtifactDto>> GetCurrentArtifactsAsync(Guid localAccountId, string remoteMessageId, CancellationToken cancellationToken = default);
    Task<IntelligenceIndexDocumentRequest?> GetPreparedDocumentAsync(Guid localAccountId, string remoteMessageId, CancellationToken cancellationToken = default);
    Task SavePreparedDocumentAsync(Guid localAccountId, string remoteMessageId, IntelligenceIndexDocumentRequest document, CancellationToken cancellationToken = default);
    Task DeletePreparedDocumentsAsync(Guid localAccountId, IReadOnlyCollection<string> remoteMessageIds, CancellationToken cancellationToken = default);
    Task ImportAsync(Guid localAccountId, Guid mailboxId, IReadOnlyList<IntelligenceArtifactDto> artifacts, long throughRevision, CancellationToken cancellationToken = default);
    Task SaveJobIntentAsync(SemanticIndexJobIntent intent, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SemanticIndexJobIntent>> GetJobIntentsAsync(CancellationToken cancellationToken = default);
    Task DeleteJobIntentAsync(Guid localAccountId, CancellationToken cancellationToken = default);
    Task DeleteMailboxAsync(Guid localAccountId, CancellationToken cancellationToken = default);
}
