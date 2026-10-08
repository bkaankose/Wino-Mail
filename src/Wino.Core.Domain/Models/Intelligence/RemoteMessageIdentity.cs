#nullable enable
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Mail.AI.Abstractions;

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>
/// Builds the canonical provider-independent identity used by Wino Intelligence.
/// </summary>
public static class RemoteMessageIdentity
{
    public static string? TryCreate(MailCopy? mail)
    {
        var account = mail?.AssignedAccount;
        var folder = mail?.AssignedFolder;
        if (mail is null || account is null || folder is null)
            return null;

        return TryCreate(
            account.ProviderType,
            mail.Id,
            folder.RemoteFolderId,
            mail.ImapUidValidity == 0 ? folder.UidValidity : mail.ImapUidValidity,
            mail.ImapUid);
    }

    public static string? TryCreate(
        MailProviderType providerType,
        string? providerMessageId,
        string? remoteFolderId,
        uint imapUidValidity,
        uint imapUid)
        => providerType switch
        {
            MailProviderType.Outlook when !string.IsNullOrWhiteSpace(providerMessageId)
                => RemoteMessageId.ForOutlook(providerMessageId),
            MailProviderType.Gmail when !string.IsNullOrWhiteSpace(providerMessageId)
                => RemoteMessageId.ForGmail(providerMessageId),
            MailProviderType.IMAP4 when !string.IsNullOrWhiteSpace(remoteFolderId) && imapUidValidity != 0 && imapUid != 0
                => RemoteMessageId.ForImap(remoteFolderId, imapUidValidity, imapUid),
            _ => null,
        };
}
