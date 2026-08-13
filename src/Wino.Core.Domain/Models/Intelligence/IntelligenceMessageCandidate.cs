#nullable enable
using System;
using System.Collections.Generic;
using Wino.Mail.AI.Abstractions;

namespace Wino.Core.Domain.Models.Intelligence;

public sealed record IntelligenceMessageCandidate(
    Guid UniqueId,
    string RemoteMessageId,
    string ProviderMessageId,
    IReadOnlyList<Guid> FileIds,
    string Subject,
    string Sender,
    string SenderName,
    DateTime ReceivedAt,
    string? ThreadId,
    bool IsOutgoing,
    bool IsRead,
    bool IsFlagged,
    bool HasAttachments,
    bool HasLaterOutgoingReply,
    string ProviderImportance,
    IReadOnlyList<string> RemoteFolderIds,
    MailBodyLocator Locator)
{
    public IntelligenceMessageCandidate(
        Guid uniqueId,
        string remoteMessageId,
        string providerMessageId,
        IReadOnlyList<Guid> fileIds,
        string subject,
        string sender,
        DateTime receivedAt,
        string? threadId,
        bool isOutgoing,
        bool hasLaterOutgoingReply,
        string providerImportance,
        IReadOnlyList<string> remoteFolderIds,
        MailBodyLocator locator)
        : this(uniqueId, remoteMessageId, providerMessageId, fileIds, subject, sender, string.Empty,
            receivedAt, threadId, isOutgoing, false, false, false, hasLaterOutgoingReply,
            providerImportance, remoteFolderIds, locator)
    {
    }
}
