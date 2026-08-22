using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wino.Mail.ViewModels.Data;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class WinoIntelligenceManagementPage : WinoIntelligenceManagementPageAbstract
{
    private bool _isApplyingToggleState;
    private bool _isApplyingIntelligencePreferenceState;

    public WinoIntelligenceManagementPage()
    {
        InitializeComponent();

        // The coverage editor hands its result back through back navigation, which only works if
        // this page and its view model survive in the back stack. Without this the page is rebuilt
        // and the whole mailbox load runs again on every return.
        NavigationCacheMode = NavigationCacheMode.Required;
    }

    private async void WinoIntelligenceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingToggleState || !ViewModel.IsPageReady || sender is not ToggleSwitch toggleSwitch)
            return;

        _isApplyingToggleState = true;
        try
        {
            var actualState = await ViewModel.SetSemanticIndexingEnabledAsync(toggleSwitch.IsOn);
            if (toggleSwitch.IsOn != actualState)
                toggleSwitch.IsOn = actualState;
        }
        finally
        {
            _isApplyingToggleState = false;
        }
    }

    private async void DailyBriefingToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingIntelligencePreferenceState || !ViewModel.IsPageReady || sender is not ToggleSwitch toggleSwitch)
            return;

        _isApplyingIntelligencePreferenceState = true;
        try
        {
            var actualState = await ViewModel.SetDailyBriefingEnabledAsync(toggleSwitch.IsOn);
            if (toggleSwitch.IsOn != actualState)
                toggleSwitch.IsOn = actualState;
        }
        finally
        {
            _isApplyingIntelligencePreferenceState = false;
        }
    }

    private async void IntelligenceIndicatorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingIntelligencePreferenceState || !ViewModel.IsPageReady ||
            sender is not ToggleSwitch toggleSwitch || toggleSwitch.DataContext is not IntelligenceIndicatorSettingsItem item)
            return;

        _isApplyingIntelligencePreferenceState = true;
        try
        {
            var actualState = await ViewModel.SetIntelligenceIndicatorVisibilityAsync(item.Identifier, toggleSwitch.IsOn);
            item.IsVisible = actualState;
            if (toggleSwitch.IsOn != actualState)
                toggleSwitch.IsOn = actualState;
        }
        finally
        {
            _isApplyingIntelligencePreferenceState = false;
        }
    }
}
