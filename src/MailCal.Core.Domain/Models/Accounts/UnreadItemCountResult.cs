using System;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Accounts;

public class UnreadItemCountResult
{
    public Guid FolderId { get; set; }
    public Guid AccountId { get; set; }
    public SpecialFolderType SpecialFolderType { get; set; }
    public int UnreadItemCount { get; set; }
}
