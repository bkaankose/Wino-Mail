#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Core.Domain.Interfaces;

public sealed record LocalIntelligenceSearchScope(
    Guid LocalAccountId,
    Guid MailboxId,
    IReadOnlySet<string> AllowedFolderIds);

public sealed record LocalIntelligenceSearchMatch(
    Guid LocalAccountId,
    string RemoteMessageId,
    MessageIntelligenceDownloadDto Document,
    double Similarity,
    double Relevance);

public interface ILocalIntelligenceSearchEngine
{
    Task<IReadOnlyList<LocalIntelligenceSearchMatch>> SearchAsync(
        IntelligenceSearchPlanResultDto response,
        IReadOnlyList<LocalIntelligenceSearchScope> scopes,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalIntelligenceSearchMatch>> FindSimilarAsync(
        Guid localAccountId,
        string sourceRemoteMessageId,
        int limit,
        bool outgoingOnly,
        IReadOnlySet<string>? excludedRemoteMessageIds = null,
        CancellationToken cancellationToken = default);
}
