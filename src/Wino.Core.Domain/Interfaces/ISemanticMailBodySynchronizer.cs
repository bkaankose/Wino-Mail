using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Intelligence;
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
    IReadOnlyList<SemanticMailAttachment> Attachments,
    /// <summary>
    /// Whether the message carried a List-Unsubscribe header. False also covers "not
    /// known": a synchronizer that fetches only a body cannot see the headers, and the
    /// consumer treats an absent header as "no unsubscribe mechanism to offer".
    /// </summary>
    bool HasListUnsubscribe = false)
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
