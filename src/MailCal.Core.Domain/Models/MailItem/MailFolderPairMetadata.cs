using System;

namespace MailCal.Core.Domain.Models.MailItem;

public class MailFolderPairMetadata
{
    public Guid FolderId { get; set; }
    public string RemoteFolderId { get; set; }
    public string MailCopyId { get; set; }
}
