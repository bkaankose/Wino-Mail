using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class WinoIntelligenceManagementPage : WinoIntelligenceManagementPageAbstract
{
    private bool _isApplyingToggleState;

    public WinoIntelligenceManagementPage() => InitializeComponent();

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
}
