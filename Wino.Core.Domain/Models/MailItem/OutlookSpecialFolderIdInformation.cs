namespace Wino.Core.Domain.Models.MailItem
{
    /// <summary>
    /// Class that holds immutable information about special folders in Outlook.
    /// </summary>
    /// <param name="InboxId"></param>
    /// <param name="TrashId"></param>
    /// <param name="JunkId"></param>
    /// <param name="DraftId"></param>
    /// <param name="SentId"></param>
    /// <param name="ArchiveId"></param>
    public record OutlookSpecialFolderIdInformation(string InboxId, string TrashId, string JunkId, string DraftId, string SentId, string ArchiveId);
}
