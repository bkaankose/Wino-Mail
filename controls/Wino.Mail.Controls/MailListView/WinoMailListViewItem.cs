using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wino.Mail.Controls.Core;

namespace Wino.Mail.Controls.MailListView;

public sealed partial class WinoMailListViewItem : ListViewItem
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row),
        typeof(MailListRow),
        typeof(WinoMailListViewItem),
        new PropertyMetadata(null));

    internal WinoMailListView? OwnerList { get; set; }

    public MailListRow? Row
    {
        get => (MailListRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        OwnerList?.RecordPointerPressed(Row, IsSelected);
        base.OnPointerPressed(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new WinoMailListViewItemAutomationPeer(this);
}

internal sealed partial class WinoMailListViewItemAutomationPeer :
    ListViewItemAutomationPeer,
    IExpandCollapseProvider
{
    private readonly WinoMailListViewItem _owner;

    public WinoMailListViewItemAutomationPeer(WinoMailListViewItem owner)
        : base(owner)
    {
        _owner = owner;
    }

    public ExpandCollapseState ExpandCollapseState => _owner.Row switch
    {
        { IsThreadHead: true, IsExpanded: false } => ExpandCollapseState.Collapsed,
        { IsThreadHead: true, IsExpanded: true } => ExpandCollapseState.Expanded,
        _ => ExpandCollapseState.LeafNode,
    };

    public void Collapse()
    {
        if (_owner.Row is { IsThreadHead: true, IsExpanded: true } row)
        {
            _owner.OwnerList?.CollapseThread(row.ThreadKey);
        }
    }

    public void Expand()
    {
        if (_owner.Row is { IsThreadHead: true, IsExpanded: false } row)
        {
            _owner.OwnerList?.ExpandThread(row.ThreadKey);
        }
    }

    protected override object? GetPatternCore(PatternInterface patternInterface)
    {
        if (patternInterface == PatternInterface.ExpandCollapse &&
            _owner.Row?.IsThreadHead == true)
        {
            return this;
        }

        return base.GetPatternCore(patternInterface);
    }

    protected override string GetNameCore()
    {
        if (AutomationProperties.GetName(_owner) is { Length: > 0 } name)
        {
            return name;
        }

        return _owner.Row switch
        {
            { IsThreadHead: true, Thread: { } thread } =>
                $"Thread {thread.Key}, {thread.Count} messages, {(thread.IsExpanded ? "expanded" : "collapsed")}",
            { SourceItem: { } item } => item.NameSortKey,
            _ => base.GetNameCore(),
        };
    }
}
