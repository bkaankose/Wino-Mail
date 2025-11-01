using System;
using System.Collections.Generic;
using System.Diagnostics;
using SQLite;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.Domain.Entities.Mail;

[DebuggerDisplay("{FolderName} - {SpecialFolderType}")]
public class MailItemFolder : IMailItemFolder
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public string RemoteFolderId { get; set; }
    public string ParentRemoteFolderId { get; set; }

    public Guid MailAccountId { get; set; }
    public string FolderName { get; set; }
    public SpecialFolderType SpecialFolderType { get; set; }
    public bool IsSystemFolder { get; set; }
    public bool IsSticky { get; set; }
    public bool IsSynchronizationEnabled { get; set; }
    public bool IsHidden { get; set; }
    public bool ShowUnreadCount { get; set; }
    public DateTime? LastSynchronizedDate { get; set; }

    // For IMAP
    public uint UidValidity { get; set; }
    public long HighestModeSeq { get; set; }

    /// <summary>
    /// Outlook shares delta changes per-folder. Gmail is for per-account.
    /// This is only used for Outlook provider.
    /// </summary>
    public string DeltaToken { get; set; }

    // For GMail Labels
    public string TextColorHex { get; set; }
    public string BackgroundColorHex { get; set; }

    [Ignore]
    public List<IMailItemFolder> ChildFolders { get; set; } = [];

    // Category and Move type folders are not valid move targets.
    // These folders are virtual. They don't exist on the server.
    public bool IsMoveTarget => !(SpecialFolderType == SpecialFolderType.More || SpecialFolderType == SpecialFolderType.Category);

    public bool ContainsSpecialFolderType(SpecialFolderType type)
    {
        if (SpecialFolderType == type)
            return true;

        foreach (var child in ChildFolders)
        {
            if (child.SpecialFolderType == type)
            {
                return true;
            }
            else
            {
                return child.ContainsSpecialFolderType(type);
            }
        }

        return false;
    }

    public static MailItemFolder CreateMoreFolder() => new MailItemFolder() { IsSticky = true, SpecialFolderType = SpecialFolderType.More, FolderName = Translator.MoreFolderNameOverride };
    public static MailItemFolder CreateCategoriesFolder() => new MailItemFolder() { IsSticky = true, SpecialFolderType = SpecialFolderType.Category, FolderName = Translator.CategoriesFolderNameOverride };

    public override string ToString() => FolderName;
}
