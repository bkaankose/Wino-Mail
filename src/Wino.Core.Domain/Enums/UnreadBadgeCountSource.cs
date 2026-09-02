namespace Wino.Core.Domain.Enums;

/// <summary>
/// Which folders of an account feed the account badge and, through it, the taskbar badge.
/// </summary>
public enum UnreadBadgeCountSource
{
    /// <summary>
    /// Only the Inbox is counted. This is how Wino has always counted, so it stays the default.
    /// </summary>
    InboxOnly,

    /// <summary>
    /// Every folder the user marked with <see cref="Entities.Mail.MailItemFolder.IsCountedInAccountTotal"/> is counted.
    /// </summary>
    SelectedFolders
}
