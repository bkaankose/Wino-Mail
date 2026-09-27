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
    Task<WinoIntelligenceOperationResult<MailTranslationResult>> TranslateAsync(WinoIntelligenceContext context, Guid requestId, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites the message in the given preset mode and returns the sanitized HTML the API sent
    /// back. Uses the same eligibility as summarize and translate.
    /// </summary>
    Task<WinoIntelligenceOperationResult<string>> RewriteAsync(WinoIntelligenceContext context, Guid requestId, string mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the composer may offer rewrite for drafts of this account: AI Pack, current consent
    /// and quota that can still be consumed. Never contacts the server.
    /// </summary>
    Task<bool> IsDraftRewriteAvailableAsync(Guid localAccountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rewrites the author's own draft (<see cref="RewriteContexts.Composing"/>). The HTML is sent
    /// whole; a draft over the API limit fails with <c>AI_HTML_TOO_LARGE</c> instead of being cut.
    /// Cancel it with <see cref="CancelRequest"/>.
    /// </summary>
    Task<WinoIntelligenceOperationResult<string>> RewriteDraftAsync(Guid localAccountId, Guid requestId, string html, string mode, CancellationToken cancellationToken = default);
    void CancelRequest(Guid requestId);
    void CancelContext(string contentKey);
    void InvalidateAccess();
}
