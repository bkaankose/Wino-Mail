using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// Editing an account: what it is used for (the capability switches of the shared <see cref="AccountCapabilitySelection"/>;
/// turning Calendar or Contacts off hides the CalDAV / CardDAV sections) and the sign-in shared by every server.
/// </summary>
internal sealed partial class ImapCalDavSettingsView
{
    private Gtk.Widget BuildEditFeatures()
    {
        var capabilities = ViewModel.Capabilities;
        var group = OnboardingUi.Group(Translator.ImapSetup_FeaturesGroup);
        group.Add(CapabilitySwitch(Translator.ProviderSelection_UseForMail, WinoIconGlyph.Mail, () => capabilities.IsMailEnabled, on => capabilities.IsMailEnabled = on));
        group.Add(CapabilitySwitch(Translator.ProviderSelection_UseForCalendar, WinoIconGlyph.Calendar, () => capabilities.IsCalendarEnabled, on => capabilities.IsCalendarEnabled = on));
        group.Add(CapabilitySwitch(Translator.ProviderSelection_UseForContacts, WinoIconGlyph.People, () => capabilities.IsContactEnabled, on => capabilities.IsContactEnabled = on));
        group.Add(CapabilitySwitch(Translator.ProviderSelection_UseForTasks, WinoIconGlyph.TaskList, () => capabilities.IsTaskEnabled, on => capabilities.IsTaskEnabled = on));
        var missing = OnboardingUi.Error(Translator.ProviderSelection_CapabilityValidationMessage);
        _form.When(missing, s => s.Capabilities.IsSelectionMissing);
        return _form.When(OnboardingUi.Column(6, group, missing), s => s.IsEditMode);
    }

    private Adw.SwitchRow CapabilitySwitch(string title, WinoIconGlyph glyph, Func<bool> read, Action<bool> write)
    {
        var row = WinoSettingsRows.Switch(title, null, glyph);
        var applying = false;
        _form.Apply(() =>
        {
            var on = read();
            if (row.GetActive() == on) return;
            applying = true;
            try { row.SetActive(on); }
            finally { applying = false; }
        });
        Bindings.Own(GtkSignals.OnNotify(row, "active", () =>
        {
            if (applying || row.GetActive() == read()) return;
            write(row.GetActive());
            _form.Request();
        }));
        return row;
    }

    private Gtk.Widget BuildEditSignIn()
    {
        var vm = ViewModel;
        var group = OnboardingUi.Group(Translator.ImapSetup_SignInGroup);
        AddIdentityRows(group, "edit-");
        var password = BuildPasswordRows("edit-password", s => s.IsEditPasswordSignInVisible);

        var reauthenticate = _form.Button(Translator.ImapSetup_OAuthSignInAgain, vm.ReauthenticateOAuthCommand);
        var spinner = _form.When(OnboardingUi.Spinner(), s => s.IsOAuthSigningIn);
        var oauthDescription = _form.Text(OnboardingUi.Caption(), s => s.EditOAuthDescription);
        var oauthHeader = OnboardingUi.Heading(Translator.ImapSetup_OAuthSignInHeader);
        var oauth = _form.When(OnboardingUi.Column(6, oauthHeader, OnboardingUi.Buttons(reauthenticate, spinner), oauthDescription), s => s.IsEditOAuthSignInVisible);

        var davScope = new WinoInfoBar(WinoInfoBarSeverity.Warning, null, Translator.ImapSetup_OAuthDavScopeSignInRequired);
        Bindings.Own(davScope);
        _form.When(davScope.Root, s => s.IsDavScopeSignInRequiredForEditVisible);
        return _form.When(OnboardingUi.Column(8, group, password, oauth, davScope.Root), s => s.IsEditSignInVisible);
    }
}
