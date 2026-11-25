using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// Editing an account, "Sign-in and servers" (Windows ImapCalDavSettingsPage in edit mode): the message of an app page
/// that asked for a server, the sign-in shared by every server with the servers it is used by, and the hints for a
/// CalDAV or CardDAV server no app uses. The apps themselves are switched on their own pages.
/// </summary>
internal sealed partial class ImapCalDavSettingsView
{
    private Gtk.Widget BuildPendingApp()
    {
        var pending = new WinoInfoBar(WinoInfoBarSeverity.Informational) { IsClosable = false };
        Bindings.Own(pending);
        _form.Apply(() => pending.Message = ViewModel.PendingAppMessage);
        return _form.When(pending.Root, s => s.IsPendingAppMessageVisible);
    }

    /// <summary>A DAV server no app uses: the server is added from the app that wants it.</summary>
    private Gtk.Widget BuildUnusedHints()
    {
        var vm = ViewModel;
        var calendar = WinoSettingsRows.Action(Translator.AccountSignIn_CalDavUnusedTitle, Translator.AccountSignIn_CalDavUnusedDescription, WinoIconGlyph.Calendar);
        WinoSettingsRows.Suffix(calendar, _form.Button(Translator.AccountSignIn_OpenCalendar, vm.OpenCalendarAppCommand));
        var people = WinoSettingsRows.Action(Translator.AccountSignIn_CardDavUnusedTitle, Translator.AccountSignIn_CardDavUnusedDescription, WinoIconGlyph.People);
        WinoSettingsRows.Suffix(people, _form.Button(Translator.AccountSignIn_OpenPeople, vm.OpenPeopleAppCommand));
        var group = OnboardingUi.Group();
        group.Add(_form.When(calendar, s => s.IsCalDavUnusedHintVisible));
        group.Add(_form.When(people, s => s.IsCardDavUnusedHintVisible));
        return _form.When(group, s => s.IsCalDavUnusedHintVisible || s.IsCardDavUnusedHintVisible);
    }

    private Gtk.Widget BuildEditSignIn()
    {
        var vm = ViewModel;
        var group = OnboardingUi.Group(Translator.ImapSetup_SignInGroup);
        AddIdentityRows(group, "edit-");
        var password = BuildPasswordRows("edit-password", s => s.IsEditPasswordSignInVisible);
        var usedBy = _form.When(_form.Optional(OnboardingUi.Caption(), s => s.SignInUsedByText), s => s.IsEditPasswordSignInVisible);

        var reauthenticate = _form.Button(Translator.ImapSetup_OAuthSignInAgain, vm.ReauthenticateOAuthCommand);
        var spinner = _form.When(OnboardingUi.Spinner(), s => s.IsOAuthSigningIn);
        var oauthDescription = _form.Text(OnboardingUi.Caption(), s => s.EditOAuthDescription);
        var oauthHeader = OnboardingUi.Heading(Translator.ImapSetup_OAuthSignInHeader);
        var oauth = _form.When(OnboardingUi.Column(6, oauthHeader, OnboardingUi.Buttons(reauthenticate, spinner), oauthDescription), s => s.IsEditOAuthSignInVisible);

        var davScope = new WinoInfoBar(WinoInfoBarSeverity.Warning, null, Translator.ImapSetup_OAuthDavScopeSignInRequired);
        Bindings.Own(davScope);
        _form.When(davScope.Root, s => s.IsDavScopeSignInRequiredForEditVisible);
        return _form.When(OnboardingUi.Column(8, group, password, usedBy, oauth, davScope.Root), s => s.IsEditSignInVisible);
    }
}
