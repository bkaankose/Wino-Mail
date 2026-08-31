using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.HoverActions;

namespace Wino.Mail.Controls.HoverActions;

public sealed partial class HoverActionsControl : UserControl
{
    private INotifyPropertyChanged? _propertyChangedSource;

    public HoverActionsControl()
    {
        InitializeComponent();
        ActualThemeChanged += OnActualThemeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    [GeneratedDependencyProperty(DefaultValue = HoverActionKind.None)]
    public partial HoverActionKind LeftAction { get; set; }

    [GeneratedDependencyProperty(DefaultValue = HoverActionKind.None)]
    public partial HoverActionKind CenterAction { get; set; }

    [GeneratedDependencyProperty(DefaultValue = HoverActionKind.None)]
    public partial HoverActionKind RightAction { get; set; }

    [GeneratedDependencyProperty]
    public partial object? ActionItem { get; set; }

    [GeneratedDependencyProperty]
    public partial object? Labels { get; set; }

    [GeneratedDependencyProperty]
    public partial ICommand? ActionCommand { get; set; }

    [GeneratedDependencyProperty]
    public partial object? ActionCommandParameter { get; set; }

    public ObservableCollection<HoverActionButtonItem> ActionItems { get; } = [];

    partial void OnLeftActionChanged(HoverActionKind newValue) => RebuildItems();

    partial void OnCenterActionChanged(HoverActionKind newValue) => RebuildItems();

    partial void OnRightActionChanged(HoverActionKind newValue) => RebuildItems();

    partial void OnLabelsChanged(object? newValue) => RebuildItems();

    partial void OnActionItemChanged(object? newValue)
    {
        DetachActionItem();
        _propertyChangedSource = newValue as INotifyPropertyChanged;
        if (_propertyChangedSource is not null)
        {
            _propertyChangedSource.PropertyChanged += OnActionItemPropertyChanged;
        }

        RefreshToggleStates();
    }

    private void RebuildItems()
    {
        if (ActionItems is null)
            return;

        var labels = Labels as HoverActionLabels ?? HoverActionLabels.Empty;
        var configuredActions = HoverActionConfiguration.GetVisibleActions(
            LeftAction,
            CenterAction,
            RightAction);

        ActionItems.Clear();
        foreach (var action in configuredActions)
        {
            ActionItems.Add(new HoverActionButtonItem(
                action,
                labels.GetLabel(action),
                GetGlyph(action)));
        }

        RefreshToggleStates();
        Visibility = ActionItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        IsHitTestVisible = ActionItems.Count > 0;
    }

    private void OnActionItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName is nameof(IHoverActionItem.IsRead) or nameof(IHoverActionItem.IsFlagged))
        {
            RefreshToggleStates();
        }
    }

    private void RefreshToggleStates()
    {
        var actionItem = ActionItem as IHoverActionItem;
        foreach (var item in ActionItems)
        {
            item.IsChecked = item.Action switch
            {
                HoverActionKind.ToggleFlag => actionItem?.IsFlagged == true,
                HoverActionKind.ToggleRead => actionItem?.IsRead == true,
                _ => false,
            };
        }
    }

    private void ActionButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HoverActionButtonItem item } ||
            ActionCommandParameter is not MailListRow row)
        {
            return;
        }

        var request = new HoverActionCommandRequest(item.Action, row);
        if (ActionCommand?.CanExecute(request) == true)
        {
            ActionCommand.Execute(request);
        }

        RefreshToggleStates();
        if (sender is ToggleButton toggleButton)
        {
            toggleButton.IsChecked = item.IsChecked;
        }
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

    private void OnActualThemeChanged(FrameworkElement sender, object args) => RefreshToggleStates();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_propertyChangedSource is null && ActionItem is INotifyPropertyChanged propertyChangedSource)
        {
            _propertyChangedSource = propertyChangedSource;
            _propertyChangedSource.PropertyChanged += OnActionItemPropertyChanged;
        }

        RebuildItems();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachActionItem();

    private void DetachActionItem()
    {
        if (_propertyChangedSource is not null)
        {
            _propertyChangedSource.PropertyChanged -= OnActionItemPropertyChanged;
            _propertyChangedSource = null;
        }
    }
}

public sealed partial class HoverActionButtonItem(
    HoverActionKind action,
    string label,
    string glyph) : INotifyPropertyChanged
{
    private bool _isChecked;

    public HoverActionKind Action { get; } = action;

    public string Label { get; } = label;

    public string Glyph { get; } = glyph;

    public string AutomationId => $"HoverAction{Action}Button";

    public bool IsChecked
    {
        get => _isChecked;
        internal set
        {
            if (_isChecked == value)
                return;

            _isChecked = value;
            PropertyChanged?.Invoke(this, new(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class HoverActionTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ButtonTemplate { get; set; }

    public DataTemplate? FlagToggleTemplate { get; set; }

    public DataTemplate? ReadToggleTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => GetItemTemplate(item);

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => GetItemTemplate(item);

    private DataTemplate? GetItemTemplate(object item) => item switch
    {
        HoverActionButtonItem { Action: HoverActionKind.ToggleFlag } => FlagToggleTemplate,
        HoverActionButtonItem { Action: HoverActionKind.ToggleRead } => ReadToggleTemplate,
        HoverActionButtonItem => ButtonTemplate,
        _ => null,
    };
}
