namespace Wino.Core.Domain.Enums;

public enum SpecialFolderType
{
    Inbox,
    Starred,
    Important,
    Sent,
    Draft,
    Archive,
    Deleted,
    Junk,
    Chat,
    Category,
    Unread,
    Forums,
    Updates,
    Personal,
    Promotions,
    Social,
    Other,
    More,

    /// <summary>The read-only Exchange public folders tree (synthetic nodes, never persisted).</summary>
    PublicFolders,

    /// <summary>The read-only Exchange online archive tree (synthetic nodes, never persisted).</summary>
    OnlineArchive
}
