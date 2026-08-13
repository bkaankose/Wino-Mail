#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Interfaces;

public sealed record IntelligenceSearchOptions(
    string Query,
    IReadOnlyList<MailItemFolder> Folders,
    int Limit = 100,
    bool? IsUnread = null,
    bool? IsFlagged = null,
    bool? HasAttachments = null);

public sealed record IntelligenceSearchOmission(Guid MailboxId, string State, string? Reason);

public sealed record IntelligenceMailSearchResult(
    IReadOnlyList<MailCopy> Items,
    IReadOnlyList<IntelligenceSearchOmission> Omissions);

public interface IIntelligenceSearchService
{
    Task<IntelligenceMailSearchResult> SearchAsync(IntelligenceSearchOptions options, CancellationToken cancellationToken = default);
}
