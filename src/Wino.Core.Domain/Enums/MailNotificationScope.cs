namespace Wino.Core.Domain.Enums;

/// <summary>
/// Which folders raise a notification when new mail arrives.
/// </summary>
public enum MailNotificationScope
{
    InboxOnly = 0,
    FocusedInboxOnly = 1,
    InboxAndCustomFolders = 2,
    AllFolders = 3
}
