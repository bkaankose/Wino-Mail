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
        if (_expansionToggleButton != null)
        {
            _expansionToggleButton.Checked += ExpansionToggleButtonChecked;
            _expansionToggleButton.Unchecked += ExpansionToggleButtonChecked;
        }

        UpdateExpansionState();
    }

    partial void OnIsPaneExpandedPropertyChanged(DependencyPropertyChangedEventArgs e)
        => UpdateExpansionState();

    private void ExpansionToggleButtonChecked(object sender, RoutedEventArgs e)
        => IsPaneExpanded = _expansionToggleButton?.IsChecked == true;

    private void UpdateExpansionState()
    {
        if (_expansionToggleButton != null && _expansionToggleButton.IsChecked != IsPaneExpanded)
            _expansionToggleButton.IsChecked = IsPaneExpanded;

        VisualStateManager.GoToState(this, IsPaneExpanded ? "CalendarExpanded" : "CalendarCollapsed", false);
    }
}
