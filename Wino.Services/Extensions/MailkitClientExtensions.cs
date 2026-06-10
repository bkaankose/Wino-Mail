using System;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Services.Extensions;

/// <summary>
/// MailKit-free helpers for the folder-local IMAP uid encoding inside MailCopy.Id
/// ({FolderId}_{UID}). MailKit-typed message helpers live in
/// Wino.Core.Extensions.MailkitMessageExtensions (companion-only).
/// </summary>
public static class MailkitClientExtensions
{
    public static char MailCopyUidSeparator = '_';

    public static uint ResolveUid(string mailCopyId)
    {
        if (string.IsNullOrWhiteSpace(mailCopyId))
            throw new ArgumentOutOfRangeException(nameof(mailCopyId), mailCopyId, "Invalid mailCopyId format.");

        var splitted = mailCopyId.Split(MailCopyUidSeparator);

        if (splitted.Length > 1 && uint.TryParse(splitted[1], out uint parsedUint)) return parsedUint;

        throw new ArgumentOutOfRangeException(nameof(mailCopyId), mailCopyId, "Invalid mailCopyId format.");
    }

    public static bool TryResolveUid(string mailCopyId, out uint uid)
    {
        uid = 0;

        if (string.IsNullOrWhiteSpace(mailCopyId))
            return false;

        var splitted = mailCopyId.Split(MailCopyUidSeparator);

        return splitted.Length > 1 && uint.TryParse(splitted[1], out uid);
    }

    public static uint ResolveUid(MailCopy mailCopy)
    {
        if (mailCopy?.ImapUid > 0)
            return mailCopy.ImapUid;

        return ResolveUid(mailCopy?.Id);
    }

    public static string CreateUid(Guid folderId, uint messageUid)
        => $"{folderId}{MailCopyUidSeparator}{messageUid}";
}
