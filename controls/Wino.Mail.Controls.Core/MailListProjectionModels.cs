using System.Collections.ObjectModel;
using System.ComponentModel;
#if WINRT_EXPOSED
using WinRT;
#endif

namespace Wino.Mail.Controls.Core;

public sealed record MailListProjectionGroupKey(bool IsPinned, object? Value);

public sealed class MailListThread
{
    internal MailListThread(string key, IReadOnlyList<IMailListSourceItem> items)
    {
        Key = key;
        Items = items;
    }

    public string Key { get; }

    public IReadOnlyList<IMailListSourceItem> Items { get; }

    public IMailListSourceItem RepresentativeItem => Items[0];

    public int Count => Items.Count;

    public bool IsExpanded { get; internal set; }
}

#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class MailListRow : INotifyPropertyChanged
{
    private MailListRow(
        MailListRowKind kind,
        IMailListSourceItem sourceItem,
        MailListThread? thread)
    {
        Kind = kind;
        SourceItem = sourceItem;
        Thread = thread;
    }

    public MailListRowKind Kind { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IMailListSourceItem SourceItem { get; }

    public MailListThread? Thread { get; }

    public string ThreadKey => Thread?.Key ?? SourceItem.ThreadKey ?? SourceItem.StableId.ToString("N");

    public bool IsThreadHead => Kind == MailListRowKind.ThreadHead;

    public bool IsThreadChild => Kind == MailListRowKind.ThreadChild;

    public bool IsExpanded => Thread?.IsExpanded == true;

    public IReadOnlyList<IMailListSourceItem> LeafItems =>
        IsThreadHead && Thread is not null
            ? Thread.Items
            : (IMailListSourceItem[])[SourceItem];

    public static MailListRow Single(IMailListSourceItem item) =>
        new(MailListRowKind.Single, item, null);

    public static MailListRow ThreadHead(MailListThread thread) =>
        new(MailListRowKind.ThreadHead, thread.RepresentativeItem, thread);

    public static MailListRow ThreadChild(MailListThread thread, IMailListSourceItem item) =>
        new(MailListRowKind.ThreadChild, item, thread);

    internal void NotifyExpansionChanged() =>
        PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
}

#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class MailListGroup : ObservableCollection<MailListRow>
{
    internal MailListGroup(object key, IEnumerable<MailListRow> rows)
        : base(rows)
    {
        Key = key;
    }

    public object Key { get; }
}

public sealed record MailListSelectionSnapshot(
    IReadOnlyList<IMailListSourceItem> SelectedItems,
    IReadOnlySet<string> FullySelectedThreadKeys,
    IMailListSourceItem? ActiveItem)
{
    public static MailListSelectionSnapshot Empty { get; } =
        new([], new HashSet<string>(StringComparer.Ordinal), null);
}

public sealed class ThreadExpansionChangedEventArgs(string threadKey, bool isExpanded) : EventArgs
{
    public string ThreadKey { get; } = threadKey;

    public bool IsExpanded { get; } = isExpanded;
}
