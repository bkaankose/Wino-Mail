using System.Windows.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.HoverActions;

namespace Wino.Mail.Controls.MailListView;

public sealed partial class WinoMailListViewItem : ListViewItem
{
    internal WinoMailListView? OwnerList { get; set; }

    [GeneratedDependencyProperty]
    public partial MailListRow? Row { get; set; }

    [GeneratedDependencyProperty(DefaultValue = HoverActionKind.None)]
    public partial HoverActionKind LeftHoverAction { get; set; }

    [GeneratedDependencyProperty(DefaultValue = HoverActionKind.None)]
    public partial HoverActionKind CenterHoverAction { get; set; }

    [GeneratedDependencyProperty(DefaultValue = HoverActionKind.None)]
    public partial HoverActionKind RightHoverAction { get; set; }

    [GeneratedDependencyProperty]
    public partial object? HoverActionLabels { get; set; }

    [GeneratedDependencyProperty]
    public partial ICommand? HoverActionCommand { get; set; }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        OwnerList?.RecordPointerPressed(Row, IsSelected);
        base.OnPointerPressed(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new WinoMailListViewItemAutomationPeer(this);
}
