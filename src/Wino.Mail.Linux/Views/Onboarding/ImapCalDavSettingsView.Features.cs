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

    /// <summary>IMAP / POP3 choice before Features / Sign in / Servers (Windows ImapSetupProtocolCard).</summary>
    private Gtk.Widget BuildProtocolStep()
    {
        var group = OnboardingUi.Group(Translator.ImapSetup_ProtocolTitle, Translator.ImapSetup_ProtocolDescription);
        group.Add(_form.Combo(Translator.ImapSetup_ProtocolTitle, s => s.AvailableProtocolDisplayNames,
            nameof(ViewModel.SelectedProtocolIndex),
            s => s.SelectedProtocolIndex,
            (s, v) => s.SelectedProtocolIndex = v));
        return _form.When(group, s => s.IsProtocolSelectionVisible);
    }

    private Gtk.Widget BuildFeaturesStep()
    {
        _capabilityPicker = new AccountCapabilityPicker(ViewModel.Capabilities, () => ViewModel.IsPop3 ? Translator.ProviderSelection_ModePop3 : Translator.ProviderSelection_ModeImap, Bindings);
        _capabilityPicker.Root.SetTitle(System.Security.SecurityElement.Escape(Translator.ImapSetup_FeaturesGroup));
        var missing = OnboardingUi.Error(Translator.ProviderSelection_CapabilityValidationMessage);
        _form.When(missing, s => s.Capabilities.IsSelectionMissing);
        return _form.When(OnboardingUi.Column(6, _capabilityPicker.Root, missing), s => s.IsFeaturesStepVisible);
    }
}
