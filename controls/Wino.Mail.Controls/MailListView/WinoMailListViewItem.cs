using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.HoverActions;
using System.Windows.Input;

namespace Wino.Mail.Controls.MailListView;

public sealed partial class WinoMailListViewItem : ListViewItem
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row),
        typeof(MailListRow),
        typeof(WinoMailListViewItem),
        new PropertyMetadata(null));

    public static readonly DependencyProperty LeftHoverActionProperty = DependencyProperty.Register(
        nameof(LeftHoverAction),
        typeof(HoverActionKind),
        typeof(WinoMailListViewItem),
        new PropertyMetadata(HoverActionKind.None));

    public static readonly DependencyProperty CenterHoverActionProperty = DependencyProperty.Register(
        nameof(CenterHoverAction),
        typeof(HoverActionKind),
        typeof(WinoMailListViewItem),
        new PropertyMetadata(HoverActionKind.None));

    public static readonly DependencyProperty RightHoverActionProperty = DependencyProperty.Register(
        nameof(RightHoverAction),
        typeof(HoverActionKind),
        typeof(WinoMailListViewItem),
        new PropertyMetadata(HoverActionKind.None));

    public static readonly DependencyProperty HoverActionLabelsProperty = DependencyProperty.Register(
        nameof(HoverActionLabels),
        typeof(object),
        typeof(WinoMailListViewItem),
        new PropertyMetadata(null));

    public static readonly DependencyProperty HoverActionCommandProperty = DependencyProperty.Register(
        nameof(HoverActionCommand),
        typeof(ICommand),
        typeof(WinoMailListViewItem),
        new PropertyMetadata(null));

    internal WinoMailListView? OwnerList { get; set; }

    public MailListRow? Row
    {
        get => (MailListRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public HoverActionKind LeftHoverAction
    {
        get => (HoverActionKind)GetValue(LeftHoverActionProperty);
        set => SetValue(LeftHoverActionProperty, value);
    }

    public HoverActionKind CenterHoverAction
    {
        get => (HoverActionKind)GetValue(CenterHoverActionProperty);
        set => SetValue(CenterHoverActionProperty, value);
    }

    public HoverActionKind RightHoverAction
    {
        get => (HoverActionKind)GetValue(RightHoverActionProperty);
        set => SetValue(RightHoverActionProperty, value);
    }

    public object? HoverActionLabels
    {
        get => GetValue(HoverActionLabelsProperty);
        set => SetValue(HoverActionLabelsProperty, value);
    }

    public ICommand? HoverActionCommand
    {
        get => (ICommand?)GetValue(HoverActionCommandProperty);
        set => SetValue(HoverActionCommandProperty, value);
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
