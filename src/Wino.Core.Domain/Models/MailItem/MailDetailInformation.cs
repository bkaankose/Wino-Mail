using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.MailItem;

public class MailDetailInformation
{
    public string Id { get; set; }

    public Guid AccountId { get; set; }
    public Guid FolderId { get; set; }
    public string RemoteFolderId { get; set; }
    public SpecialFolderType SpecialFolderType { get; set; }
    public bool IsRead { get; set; }
    public bool IsFlagged { get; set; }
    public bool IsDraft { get; set; }
}
