using System;
using System.Collections.Generic;
using System.Diagnostics;
using SQLite;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.Domain.Entities.Mail;

[DebuggerDisplay("{ResolvedDisplayName} - {SpecialFolderType}")]
public class MailItemFolder : IMailItemFolder
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public string RemoteFolderId { get; set; }
    public string ParentRemoteFolderId { get; set; }

    public Guid MailAccountId { get; set; }
    public string FolderName { get; set; }

    /// <summary>
    /// Local-only name the user chose for the folder. It is never sent to the server; empty shows <see cref="FolderName"/>.
    /// </summary>
    public string DisplayName { get; set; }

    [Ignore]
    public string ResolvedDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? FolderName : DisplayName;

    /// <summary>
    /// Name with the owner of a shared folder, for places that list it outside its owner's tree (pinned, flat lists).
    /// </summary>
    [Ignore]
    public string QualifiedDisplayName => !string.IsNullOrWhiteSpace(DisplayName) || string.IsNullOrWhiteSpace(OwnerName)
        ? ResolvedDisplayName
        : string.Format(Translator.SharedFolder_NameWithOwner, FolderName, OwnerName);

    /// <summary>
    /// The mailbox owner of a folder outside the personal namespace. Filled by the folder service when it reads
    /// folders; empty for personal folders and for the owner's own node.
    /// </summary>
    [Ignore]
    public string OwnerName { get; set; }

    /// <summary>
    /// The level of a shared tree that stands for its owner's mailbox. It may be a container or, when the owner shares
    /// the INBOX, that INBOX itself; either way it reads as the owner. Filled by the folder service.
    /// </summary>
    [Ignore]
    public bool IsSharedOwnerNode { get; set; }

    /// <summary>
    /// A namespace level ("Shared Folders", "shared") the folder list skips because only one mailbox is below it.
    /// Filled by the folder service.
    /// </summary>
    [Ignore]
    public bool IsSkippedSharedLevel { get; set; }

    /// <remarks>Rows stored before this column existed read as <see cref="MailFolderScope.Personal"/>.</remarks>
    public MailFolderScope Scope { get; set; }

    /// <summary>
    /// A level of a shared tree that cannot be opened (\Noselect, or missing on the server). It only groups its
    /// children: it is never synchronized, selected, renamed, deleted or used as a move target.
    /// </summary>
    public bool IsContainer { get; set; }

    /// <summary>
    /// The user's rights on a folder outside the personal namespace (RFC 4314 MYRIGHTS letters, for example "lrswi"),
    /// read on folder synchronization when the server supports ACL. Null when unknown, which allows everything.
    /// </summary>
    public string MyRights { get; set; }

    /// <summary>Whether the folder's rights are known and include the RFC 4314 right.</summary>
    public bool HasKnownRight(char right) => MyRights != null && MyRights.Contains(right);

    private bool AllowsRight(char right) => MyRights == null || MyRights.Contains(right);

    public SpecialFolderType SpecialFolderType { get; set; }
    public bool IsSystemFolder { get; set; }
    public bool IsSticky { get; set; }
    public bool IsSynchronizationEnabled { get; set; }
    public bool IsHidden { get; set; }
    public bool ShowUnreadCount { get; set; }

    /// <summary>
    /// Whether this folder feeds the account unread total, and through it the taskbar badge.
    /// Only honored when the account count source is <see cref="Enums.UnreadBadgeCountSource.SelectedFolders"/>.
    /// Seeded from <see cref="ShowUnreadCount"/> on upgrade so existing badges do not change.
    /// </summary>
    public bool IsCountedInAccountTotal { get; set; }

    public bool IsJumpListEnabled { get; set; }

    // User-defined ordering within its navigation section (Pinned / Categories / More).
    // 0 means "no custom order set" — the folder falls back to the default sort
    // (alphabetic for More, canonical SpecialFolderType order as a tiebreak for Pinned).
    public int Order { get; set; }
    public DateTime? LastSynchronizedDate { get; set; }

    // For IMAP
    public uint UidValidity { get; set; }
    public long HighestModeSeq { get; set; }
    public uint HighestKnownUid { get; set; }
    public DateTime? LastUidReconcileUtc { get; set; }

    /// <summary>
    /// Outlook shares delta changes per-folder. Gmail is for per-account.
    /// This is only used for Outlook provider.
    /// </summary>
    public string DeltaToken { get; set; }

    /// <summary>
    /// The Exchange ActiveSync folder type of a mail folder synchronized over ActiveSync, 0 for other transports.
    /// Only mail types (1 to 6 and 12) are stored here; other collections live in EasCollectionState.
    /// </summary>
    public int EasFolderType { get; set; }
    public string EasSyncGeneration { get; set; } = string.Empty;

    /// <summary>
    /// The MAPI folder id (16 hex digits) for on-premises Exchange accounts synchronized over MAPI over HTTP.
    /// Rows keyed by an EWS RemoteFolderId are re-keyed in place through this column when the transport changes.
    /// </summary>
    public string MapiFolderId { get; set; }

    // For GMail Labels
    public string TextColorHex { get; set; }
    public string BackgroundColorHex { get; set; }

    [Ignore]
    public List<IMailItemFolder> ChildFolders { get; set; } = [];

    /// <summary>A folder that holds mail and can be opened: not a virtual group (More, Categories) or a container.</summary>
    public bool IsSelectable => !IsContainer && !(SpecialFolderType == SpecialFolderType.More || SpecialFolderType == SpecialFolderType.Category);

    /// <summary>A folder mail can be moved or copied into: selectable, and the RFC 4314 insert right when rights are known.</summary>
    public bool IsMoveTarget => IsSelectable && AllowsRight('i');

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

    public override string ToString() => ResolvedDisplayName;
}
