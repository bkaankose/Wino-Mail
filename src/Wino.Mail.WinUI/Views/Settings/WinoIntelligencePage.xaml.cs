using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Mail.WinUI;
using Wino.Mail.WinUI.Interfaces;
using Wino.Views.Abstract;
using WinRT;

namespace Wino.Views.Settings;

public sealed partial class WinoIntelligencePage : WinoIntelligencePageAbstract
{
    private bool _isApplyingConsentState;

    public WinoIntelligencePage() => InitializeComponent();

    [DynamicWindowsRuntimeCast(typeof(ToggleSwitch))]
    private async void IntelligenceConsentToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isApplyingConsentState || ViewModel.IsConsentBusy ||
            sender is not ToggleSwitch toggle || toggle.IsOn == ViewModel.IsConsentGranted)
        {
            return;
        }

        _isApplyingConsentState = true;
        try
        {
            var dialogService = WinoApplication.Current.Services.GetRequiredService<IWinoDialogService>();

            if (toggle.IsOn)
            {
                await dialogService.ShowIntelligenceConsentPolicyDialogAsync(
                    ViewModel.ConsentPolicyUri,
                    () => ViewModel.SetIntelligenceConsentAsync(true));
            }
            else
            {
                var isConfirmed = await dialogService.ShowCautiousConfirmationDialogAsync(
                    Translator.WinoAccount_IntelligenceConsentDisableTitle,
                    Translator.WinoAccount_IntelligenceConsentDisableMessage,
                    Translator.WinoAccount_DisableConsent);

                if (isConfirmed)
                    await ViewModel.SetIntelligenceConsentAsync(false);
            }

            toggle.IsOn = ViewModel.IsConsentGranted;
        }
        finally
        {
            _isApplyingConsentState = false;
        }
    }
}
