#if WINRT_EXPOSED
using WinRT;
#endif

namespace Wino.Mail.Controls.Core;

public enum MailListSortMode
{
    Date,
    Name,
    None,
}

public enum MailListGroupMode
{
    Date,
    Name,
    None,
    DayGroups,
}

public enum ThreadMessageOrder
{
    NewestFirst,
    OldestFirst,
}

public enum MailListRowKind
{
    Single,
    ThreadHead,
    ThreadChild,
}

public enum MailListSelectionState
{
    None,
    Partial,
    All,
}

#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial record MailListProjectionOptions
{
    public MailListSortMode SortMode { get; init; } = MailListSortMode.Date;

    public MailListGroupMode GroupMode { get; init; } = MailListGroupMode.Date;

    public ThreadMessageOrder ThreadMessageOrder { get; init; } = ThreadMessageOrder.NewestFirst;

    public bool IsThreadingEnabled { get; init; } = true;

    public bool IsPinnedFirst { get; init; } = true;

    /// <summary>
    /// The local day that <see cref="MailListGroupMode.DayGroups"/> measures from. Null uses the
    /// current day, read once per rebuild.
    /// </summary>
    public DateTime? ReferenceDate { get; init; }
}
