namespace Wino.Core.Domain.Models.MailItem;

public sealed record MailListGroupKey(bool IsPinned, object Value, bool IsGroupless = false)
{
    public static MailListGroupKey Pinned { get; } = new(true, null);
    public static MailListGroupKey Groupless { get; } = new(false, null, true);
}
