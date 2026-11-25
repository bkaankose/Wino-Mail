using Wino.Core.Domain;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// Adding an account before its apps were chosen (Windows ImapCalDavSettingsPage Features step, macOS BuildFeaturesStep):
/// the capability cards over the shared <see cref="AccountCapabilitySelection"/>, shown only while the setup starts on
/// Features. The picker is built once, so Back to this step finds the cards as they were left.
/// </summary>
internal sealed partial class ImapCalDavSettingsView
{
    private AccountCapabilityPicker? _capabilityPicker;

    private Gtk.Widget BuildFeaturesStep()
    {
        _capabilityPicker = new AccountCapabilityPicker(ViewModel.Capabilities, () => Translator.ProviderSelection_ModeImap, Bindings);
        _capabilityPicker.Root.SetTitle(System.Security.SecurityElement.Escape(Translator.ImapSetup_FeaturesGroup));
        var missing = OnboardingUi.Error(Translator.ProviderSelection_CapabilityValidationMessage);
        _form.When(missing, s => s.Capabilities.IsSelectionMissing);
        return _form.When(OnboardingUi.Column(6, _capabilityPicker.Root, missing), s => s.IsFeaturesStepVisible);
    }
}
