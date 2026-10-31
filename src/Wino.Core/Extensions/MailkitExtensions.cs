using System;
using MailKit;
using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Extensions;

public static class MailkitExtensions
{
    public static MailItemFolder GetLocalFolder(this IMailFolder mailkitMailFolder)
    {
        return new MailItemFolder()
        {
            Id = Guid.NewGuid(),
            FolderName = mailkitMailFolder.GetLocalFolderName(),
            RemoteFolderId = mailkitMailFolder.FullName,
            ParentRemoteFolderId = mailkitMailFolder.ParentFolder?.FullName,
            SpecialFolderType = Domain.Enums.SpecialFolderType.Other
        };
    }

    /// <summary>
    /// Keeps the server's folder name as is. Only the top-level INBOX, a reserved case-insensitive
    /// name rather than one the user chose (RFC 3501 5.1), is shown as "Inbox".
    /// </summary>
    public static string GetLocalFolderName(this IMailFolder mailkitMailFolder)
    {
        var name = mailkitMailFolder.Name ?? string.Empty;

        return string.Equals(mailkitMailFolder.FullName, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? "Inbox"
            : name;
    }
}
