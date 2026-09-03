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
        ApplyPosition();
        UpdateActionItems();
    }

    [GeneratedDependencyProperty(DefaultValue = HoverActionPosition.RightCenter)]
    public partial HoverActionPosition Position { get; set; }

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

    [GeneratedDependencyProperty(DefaultValue = HoverActionButtonSize.Small)]
    public partial HoverActionButtonSize ButtonSize { get; set; }

    public ObservableCollection<HoverActionButtonItem> ActionItems { get; } = [];

    partial void OnLeftActionPropertyChanged(DependencyPropertyChangedEventArgs e) => UpdateActionItems();

    partial void OnCenterActionPropertyChanged(DependencyPropertyChangedEventArgs e) => UpdateActionItems();

    partial void OnRightActionPropertyChanged(DependencyPropertyChangedEventArgs e) => UpdateActionItems();

    partial void OnMailRowPropertyChanged(DependencyPropertyChangedEventArgs e) => UpdateActionItems();

    partial void OnLabelsPropertyChanged(DependencyPropertyChangedEventArgs e) => UpdateActionItems();

    partial void OnActionCommandPropertyChanged(DependencyPropertyChangedEventArgs e) => UpdateActionItems();

    partial void OnButtonSizePropertyChanged(DependencyPropertyChangedEventArgs e) => UpdateActionItems();

    partial void OnPositionPropertyChanged(DependencyPropertyChangedEventArgs e) => ApplyPosition();

    private void ApplyPosition()
    {
        HorizontalAlignment = Position switch
        {
            HoverActionPosition.TopCenter or HoverActionPosition.BottomCenter => HorizontalAlignment.Center,
            _ => HorizontalAlignment.Right,
        };

        VerticalAlignment = Position switch
        {
            HoverActionPosition.RightTop or HoverActionPosition.TopCenter => VerticalAlignment.Top,
            HoverActionPosition.RightBottom or HoverActionPosition.BottomCenter => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Center,
        };
    }

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
                ButtonSize,
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
