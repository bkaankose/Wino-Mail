using System;
using Wino.Domain.Enums;

namespace Wino.Domain.Models.Accounts
{
    public class UnreadItemCountResult
    {
        public Guid FolderId { get; set; }
        public Guid AccountId { get; set; }
        public SpecialFolderType SpecialFolderType { get; set; }
        public int UnreadItemCount { get; set; }
    }
}
