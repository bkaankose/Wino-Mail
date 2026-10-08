using System;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Extensions;

public static class MailConversationIdentity
{
    public static Guid AccountId(MailCopy mail) =>
        mail.AssignedAccount?.Id ?? mail.AssignedFolder?.MailAccountId ?? Guid.Empty;

    public static (Guid AccountId, string MessageId, Guid CopyId) MessageKey(MailCopy mail) =>
        mail.AssignedAccount?.ProviderType == MailProviderType.Gmail &&
        AccountId(mail) != Guid.Empty && !string.IsNullOrWhiteSpace(mail.Id)
            ? (AccountId(mail), mail.Id, Guid.Empty)
            : (Guid.Empty, null, mail.UniqueId);

    public static string ThreadKey(MailCopy mail) =>
        string.IsNullOrWhiteSpace(mail.ThreadId) ? null : $"{AccountId(mail):N}:{mail.ThreadId}";

    // Stable mailbox copies outlive state labels such as UNREAD and STARRED.
    public static int CopyRank(MailCopy mail) => mail.AssignedFolder?.SpecialFolderType switch
    {
        SpecialFolderType.Inbox or SpecialFolderType.Sent or SpecialFolderType.Draft or SpecialFolderType.Archive => 0,
        SpecialFolderType.Unread or SpecialFolderType.Starred => 2,
        _ => 1,
    };
}
