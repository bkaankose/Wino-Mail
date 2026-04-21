namespace Wino.Core.Domain.Models.MailItem;

public sealed record MailListGroupKey(bool IsPinned, object Value)
{
    public static MailListGroupKey Pinned { get; } = new(true, null);
}
