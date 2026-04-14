using System;

namespace Wino.Core.Domain.Models.Accounts;

public class UnreadCategoryCountResult
{
    public Guid CategoryId { get; set; }
    public Guid AccountId { get; set; }
    public int UnreadItemCount { get; set; }
}
