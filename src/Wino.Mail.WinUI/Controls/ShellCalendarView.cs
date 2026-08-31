using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;

namespace Wino.Mail.WinUI.Controls;

/// <summary>
/// Calendar pane date picker with strongly typed template state for trimming and Native AOT.
/// </summary>
[Bindable]
public partial class ShellCalendarView : CalendarView
{
    private ToggleButton? _expansionToggleButton;
    private FrameworkElement? _calendarViews;
    private bool _isInitialCollapsePending;

    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsPaneExpanded { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string ExpansionAutomationName { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string ExpansionToolTip { get; set; }

    [GeneratedDependencyProperty(DefaultValue = "")]
    public partial string ExpansionGlyph { get; set; }

    protected override void OnApplyTemplate()
    {
        if (_expansionToggleButton != null)
        {
            _expansionToggleButton.Checked -= ExpansionToggleButtonChecked;
            _expansionToggleButton.Unchecked -= ExpansionToggleButtonChecked;
        }

        base.OnApplyTemplate();

        _expansionToggleButton = GetTemplateChild("DatePickerExpansionToggleButton") as ToggleButton;
        _calendarViews = GetTemplateChild("Views") as FrameworkElement;
        if (_expansionToggleButton != null)
        {
            _expansionToggleButton.Checked += ExpansionToggleButtonChecked;
            _expansionToggleButton.Unchecked += ExpansionToggleButtonChecked;
        }

        Loaded -= ShellCalendarViewLoaded;
        Loaded += ShellCalendarViewLoaded;

        if (IsPaneExpanded)
        {
            UpdateExpansionState();
        }
        else
        {
            // CalendarView initializes its virtualizing panels during the first measure. Keep
            // those template parts visible for that pass; collapsing them here leaves the
            // month panel with no viewport and it never realizes its days when later expanded.
            _isInitialCollapsePending = true;
            VisualStateManager.GoToState(this, "CalendarExpanded", false);

            if (_expansionToggleButton != null)
                _expansionToggleButton.IsChecked = false;

            if (IsLoaded)
                ScheduleInitialCollapse();
        }
    }

    private void ShellCalendarViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialCollapsePending)
            ScheduleInitialCollapse();
    }

    private void ScheduleInitialCollapse()
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isInitialCollapsePending)
                return;

            UpdateLayout();
            DispatcherQueue.TryEnqueue(CompleteInitialCollapse);
        });

    private void CompleteInitialCollapse()
    {
        if (!_isInitialCollapsePending)
            return;

        _isInitialCollapsePending = false;

        if (!IsPaneExpanded)
            VisualStateManager.GoToState(this, "CalendarCollapsed", false);
    }

    partial void OnIsPaneExpandedPropertyChanged(DependencyPropertyChangedEventArgs e)
        => UpdateExpansionState();

    private void ExpansionToggleButtonChecked(object sender, RoutedEventArgs e)
        => IsPaneExpanded = _expansionToggleButton?.IsChecked == true;

    private void UpdateExpansionState()
    {
        if (IsPaneExpanded)
            _isInitialCollapsePending = false;

        if (_expansionToggleButton != null && _expansionToggleButton.IsChecked != IsPaneExpanded)
            _expansionToggleButton.IsChecked = IsPaneExpanded;

        VisualStateManager.GoToState(this, IsPaneExpanded ? "CalendarExpanded" : "CalendarCollapsed", false);

        if (IsPaneExpanded)
        {
            DispatcherQueue.TryEnqueue(RealizeExpandedDates);
        }
    }

    private void RealizeExpandedDates()
    {
        if (!IsPaneExpanded || !IsLoaded)
            return;

        _calendarViews?.InvalidateMeasure();
        InvalidateMeasure();
        UpdateLayout();
    }
}
