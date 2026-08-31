using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.HoverActions;

namespace Wino.Mail.Controls.HoverActions;

public sealed partial class HoverActionsControl : UserControl
{
    public HoverActionsControl()
    {
        InitializeComponent();
        UpdateActionItems();
    }

    [GeneratedDependencyProperty(DefaultValue = HoverActionKind.None)]
    public partial HoverActionKind LeftAction { get; set; }

    [GeneratedDependencyProperty(DefaultValue = HoverActionKind.None)]
    public partial HoverActionKind CenterAction { get; set; }

    [GeneratedDependencyProperty(DefaultValue = HoverActionKind.None)]
    public partial HoverActionKind RightAction { get; set; }

    [GeneratedDependencyProperty]
    public partial MailListRow? MailRow { get; set; }

    [GeneratedDependencyProperty]
    public partial object? Labels { get; set; }

    [GeneratedDependencyProperty]
    public partial ICommand? ActionCommand { get; set; }

    public ObservableCollection<HoverActionButtonItem> ActionItems { get; } = [];

    partial void OnLeftActionPropertyChanged(DependencyPropertyChangedEventArgs e) => UpdateActionItems();

    partial void OnCenterActionPropertyChanged(DependencyPropertyChangedEventArgs e) => UpdateActionItems();

    partial void OnRightActionPropertyChanged(DependencyPropertyChangedEventArgs e) => UpdateActionItems();

    partial void OnMailRowPropertyChanged(DependencyPropertyChangedEventArgs e) => UpdateActionItems();

    partial void OnLabelsPropertyChanged(DependencyPropertyChangedEventArgs e) => UpdateActionItems();

    partial void OnActionCommandPropertyChanged(DependencyPropertyChangedEventArgs e) => UpdateActionItems();

    private void UpdateActionItems()
    {
        if (ActionItems is null)
        {
            return;
        }

        ActionItems.Clear();

        if (MailRow is null)
        {
            Visibility = Visibility.Collapsed;
            IsHitTestVisible = false;
            return;
        }

        var labels = Labels as HoverActionLabels ?? HoverActionLabels.Default;
        foreach (var action in HoverActionConfiguration.GetVisibleActions(
                     LeftAction,
                     CenterAction,
                     RightAction))
        {
            ActionItems.Add(new HoverActionButtonItem(
                action,
                labels.GetLabel(action),
                GetGlyph(action),
                ActionCommand,
                new HoverActionCommandRequest(action, MailRow)));
        }

        var hasActions = ActionItems.Count > 0;
        Visibility = hasActions ? Visibility.Visible : Visibility.Collapsed;
        IsHitTestVisible = hasActions;
    }

    private static string GetGlyph(HoverActionKind action) => action switch
    {
        HoverActionKind.Archive => "\uE066",
        HoverActionKind.Delete => "\uEEA6",
        HoverActionKind.ToggleFlag => "\uF40C",
        HoverActionKind.ToggleRead => "\uF522",
        HoverActionKind.MoveToJunk => "\uF140",
        _ => string.Empty,
    };
}
