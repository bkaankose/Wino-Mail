namespace Wino.Core.Domain.Enums
{
    public enum ChangeRequestType
    {
        MailMarkAs,
        MailChangeFlag,
        MailHardDelete,
        MailMove,
        MailAlwaysMoveTo,
        MailChangeFocused,
        MailArchive,
        MailUnarchive,
        FolderMarkAsRead,
        FolderDelete,
        FolderEmpty,
        FolderRename,
        CreateNewDraft,
        CreateReplyDraft,
        CreateForwardDraft,
        DiscardDraft,
        SendDraft,
        FetchSingleItem
    }
}
