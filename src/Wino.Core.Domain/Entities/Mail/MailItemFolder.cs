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
    /// The MAPI folder id (16 hex digits) for on-premises Exchange accounts synced over MAPI/HTTP.
    /// Rows keyed by an EWS RemoteFolderId are re-keyed in place through this column.
    /// </summary>
    public string MapiFolderId { get; set; }

    // For GMail Labels
    public string TextColorHex { get; set; }
    public string BackgroundColorHex { get; set; }

    [Ignore]
    public List<IMailItemFolder> ChildFolders { get; set; } = [];

    // Read-only remote trees (Exchange public folders and the online archive) are synthetic nodes that are
    // never persisted. The hierarchy is walked lazily and content is fetched live, so a node carries only
    // what the navigation and the live reads need: RemoteFolderId holds the provider's folder id.

    /// <summary>A node of the read-only Exchange public folders tree.</summary>
    [Ignore]
    public bool IsPublicFolderNode { get; set; }

    /// <summary>A node of the read-only Exchange online archive tree.</summary>
    [Ignore]
    public bool IsOnlineArchiveNode { get; set; }

    /// <summary>The surface a remote read-only node belongs to, derived from its container class.</summary>
    [Ignore]
    public PublicFolderKind PublicFolderKind { get; set; }

    /// <summary>A throwaway "Loading" or informational child that gives a lazy remote node its expander.</summary>
    [Ignore]
    public bool IsPublicFolderPlaceholder { get; set; }

    /// <summary>True for any read-only remote tree node (public folders or online archive).</summary>
    [Ignore]
    public bool IsRemoteReadOnlyNode => IsPublicFolderNode || IsOnlineArchiveNode;

    // Category, More and remote read-only folders are not valid move targets.
    // These folders are virtual or read-only. They don't exist on the server as mailbox folders.
    public bool IsMoveTarget => !(SpecialFolderType is SpecialFolderType.More or SpecialFolderType.Category or SpecialFolderType.PublicFolders or SpecialFolderType.OnlineArchive)
                                && !IsRemoteReadOnlyNode;

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
