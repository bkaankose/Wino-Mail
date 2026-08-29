using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.AppModeSwitcher;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class AppModeSwitcherPage : Page
{
    /// <summary>
    /// The last entry in the selection list is the "nothing selected" state, which the
    /// control expresses as -1 rather than as a fifth item.
    /// </summary>
    private const int NoSelectionOptionIndex = 4;

    public AppModeSwitcherPage()
    {
        InitializeComponent();
    }

    private void StateChanged(object sender, RoutedEventArgs e) => ApplyState();

    private void StateChanged(object sender, TextChangedEventArgs e) => ApplyState();

    private void SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyState();

    /// <summary>
    /// The control reports the invocation and leaves the selection alone, so the page has to
    /// move it. That is the same contract the shell honours, and driving the page this way
    /// exercises the events rather than only the properties.
    /// </summary>
    private void SwitcherModeInvoked(object? sender, WinoAppModeInvokedEventArgs e)
    {
        SettingsSelectedToggle.IsOn = false;
        SelectionButtons.SelectedIndex = e.Index;
    }

    private void SwitcherSettingsInvoked(object? sender, EventArgs e)
    {
        SelectionButtons.SelectedIndex = NoSelectionOptionIndex;
        SettingsSelectedToggle.IsOn = true;
    }

    private void ApplyState()
    {
        if (PaneSwitcher is null || RailSwitcher is null || SettingsLabelBox is null)
        {
            return;
        }

        var selectedIndex = SelectionButtons.SelectedIndex == NoSelectionOptionIndex
            ? -1
            : SelectionButtons.SelectedIndex;

        foreach (var switcher in new[] { PaneSwitcher, RailSwitcher })
        {
            switcher.SelectedIndex = selectedIndex;
            switcher.IsSettingsSelected = SettingsSelectedToggle.IsOn;
            switcher.IsSettingsVisible = SettingsVisibleToggle.IsOn;
            switcher.SettingsLabel = SettingsLabelBox.Text;
        }
    }
}
