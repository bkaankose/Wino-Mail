using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Mail.AI.Abstractions;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Retrieves only the visible message body through the authenticated provider
/// client already owned by an account synchronizer.
/// </summary>
public interface ISemanticMailBodySynchronizer
{
    Task<SemanticMailContent> GetSemanticBodyAsync(
        MailBodyLocator locator,
        CancellationToken cancellationToken = default);
}

public sealed record SemanticMailContent(
    MailBodyContent Body,
    IReadOnlyList<MailAddress> From,
    IReadOnlyList<string> ToRecipients,
    IReadOnlyList<string> CcRecipients,
    IReadOnlyList<SemanticMailAttachment> Attachments)
{
    public SemanticMailContent(
        MailBodyContent body,
        IReadOnlyList<MailAddress> from,
        IReadOnlyList<string> toRecipients,
        IReadOnlyList<string> ccRecipients)
        : this(body, from, toRecipients, ccRecipients, [])
    {
    }

    public SemanticMailContent(MailBodyContent body, IReadOnlyList<string> toRecipients, IReadOnlyList<string> ccRecipients)
        : this(body, [], toRecipients, ccRecipients, [])
    {
    }
}

public sealed record SemanticMailAttachment(string FileName, string MediaType);
