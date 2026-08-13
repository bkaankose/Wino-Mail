#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.AI.Abstractions;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoIntelligenceCoordinator
{
    Task<WinoIntelligenceSnapshot> GetSnapshotAsync(WinoIntelligenceContext context, CancellationToken cancellationToken = default);
    Task RequestProcessingAsync(WinoIntelligenceContext context, CancellationToken cancellationToken = default);
    Task<WinoIntelligenceOperationResult<string>> SummarizeAsync(WinoIntelligenceContext context, Guid requestId, CancellationToken cancellationToken = default);
    Task<WinoIntelligenceOperationResult<IReadOnlyList<WinoSuggestedReply>>> GetSuggestedRepliesAsync(WinoIntelligenceContext context, Guid requestId, CancellationToken cancellationToken = default);
    Task<WinoIntelligenceOperationResult<MailTranslationResult>> TranslateAsync(WinoIntelligenceContext context, Guid requestId, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default);
    Task<WinoIntelligenceOperationResult<IReadOnlyList<WinoSimilarMailItem>>> FindSimilarAsync(WinoIntelligenceContext context, Guid requestId, CancellationToken cancellationToken = default);
    Task<Guid> CreateSuggestedReplyDraftAsync(WinoIntelligenceContext context, string replyText, CancellationToken cancellationToken = default);
    void CancelRequest(Guid requestId);
    void CancelContext(string contentKey);
    void InvalidateAccess();
}
