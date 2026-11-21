using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// Secure sign-in on the sign-in step: the server's own OAuth offer (the browser opens only when the user clicks), the
/// signed-in card, and the organisation's own OAuth client when Wino has none for the server. The client id here is one
/// the user types for their organisation; nothing is prefilled.
/// </summary>
internal sealed partial class ImapCalDavSettingsView
{
    private Gtk.Widget BuildOAuthChoice()
    {
        var vm = ViewModel;
        var title = _form.Text(OnboardingUi.Heading(), s => s.SecureSignInTitle);
        var description = OnboardingUi.Caption(Translator.ImapSetup_SecureSignInDescription);

        var confirm = _form.Check(string.Empty, nameof(vm.IsIssuerConfirmed), s => s.IsIssuerConfirmed, (s, v) => s.IsIssuerConfirmed = v);
        _form.Apply(() =>
        {
            if (confirm.GetLabel() != ViewModel.IssuerConfirmationText) confirm.SetLabel(ViewModel.IssuerConfirmationText);
        });
        var confirmDescription = OnboardingUi.Caption(Translator.ImapSetup_OAuthConfirmIssuerDescription);
        confirmDescription.SetMarginStart(28);
        var confirmation = _form.When(OnboardingUi.Column(2, confirm, confirmDescription), s => s.IsIssuerConfirmationVisible);

        var signIn = OnboardingUi.Button(string.Empty, suggested: true);
        _form.Command(signIn, vm.SignInWithOAuthCommand);
        _form.Apply(() =>
        {
            if (signIn.GetLabel() == ViewModel.OAuthSignInButtonText) return;
            signIn.SetLabel(ViewModel.OAuthSignInButtonText ?? string.Empty);
            WinoA11y.Rename(signIn, ViewModel.OAuthSignInButtonText ?? string.Empty);
        });
        var issuer = _form.Text(OnboardingUi.Caption(), s => s.OAuthIssuerHost);
        var action = OnboardingUi.Column(4, OnboardingUi.Buttons(signIn, SigningInSpinner()), issuer);

        var card = Card(WinoIconGlyph.LockClosed, "wino-setup-card-accent", OnboardingUi.Column(8, title, description, confirmation, action));
        var usePassword = _form.Link(Translator.ImapSetup_UsePasswordInstead, vm.UsePasswordInsteadCommand);
        return _form.When(OnboardingUi.Column(8, card, usePassword), s => s.IsOAuthChoiceVisible);
    }

    /// <summary>Signed in: the session stays in memory until the account is saved.</summary>
    private Gtk.Widget BuildOAuthSignedIn()
    {
        var vm = ViewModel;
        var title = _form.Text(OnboardingUi.Heading(), s => s.OAuthSignedInTitle);
        var description = _form.Text(OnboardingUi.Caption(), s => s.OAuthSignedInDescription);
        var text = OnboardingUi.Column(2, title, description);
        text.SetHexpand(true);
        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        row.Append(text);
        row.Append(_form.Link(Translator.ImapSetup_OAuthSignInAgain, vm.SignInAgainCommand));
        var card = Card(WinoIconGlyph.CheckmarkCircle, "wino-setup-card-success", row);

        var noRefresh = new WinoInfoBar(WinoInfoBarSeverity.Warning, null, Translator.ImapSetup_OAuthNoRefreshToken);
        Bindings.Own(noRefresh);
        _form.When(noRefresh.Root, s => s.IsNoRefreshTokenWarningVisible);
        var davScope = new WinoInfoBar(WinoInfoBarSeverity.Warning, null, Translator.ImapSetup_OAuthDavScopeSignInRequired);
        Bindings.Own(davScope);
        _form.When(davScope.Root, s => s.IsDavScopeSignInRequiredForSetupVisible);
        var usePassword = _form.Link(Translator.ImapSetup_UsePasswordInstead, vm.UsePasswordInsteadCommand);
        return _form.When(OnboardingUi.Column(8, card, noRefresh.Root, davScope.Root, usePassword), s => s.IsOAuthSignedInVisible);
    }

    /// <summary>The server offers OAuth but Wino has no client for it: the organisation registers one with these redirect addresses.</summary>
    private Gtk.Widget BuildOrganizationSignIn()
    {
        var vm = ViewModel;
        var group = OnboardingUi.Group(Translator.ImapSetup_OrganizationSignIn, Translator.ImapSetup_OrganizationSignInDescription);
        group.Add(RedirectRow(vm.OAuthCustomSchemeRedirectUri));
        group.Add(RedirectRow(vm.OAuthLoopbackRedirectUri));
        group.Add(Field("oauth-issuer", _form.Entry(Translator.ImapSetup_OAuthIssuerHeader, nameof(vm.CustomOAuthIssuer), s => s.CustomOAuthIssuer, (s, v) => s.CustomOAuthIssuer = v, Gtk.InputPurpose.Url)));
        group.Add(Field("oauth-client", _form.Entry(Translator.ImapSetup_OAuthClientIdHeader, nameof(vm.CustomOAuthClientId), s => s.CustomOAuthClientId, (s, v) => s.CustomOAuthClientId = v)));
        group.Add(Field("oauth-scopes", _form.Entry(Translator.ImapSetup_OAuthScopesHeader, nameof(vm.CustomOAuthScopes), s => s.CustomOAuthScopes, (s, v) => s.CustomOAuthScopes = v)));
        var signIn = _form.Button(Translator.ImapSetup_OAuthSignInWithClient, vm.SignInWithOrganizationClientCommand);
        return _form.When(OnboardingUi.Column(8, group, OnboardingUi.Buttons(signIn, SigningInSpinner())), s => s.IsOrganizationSignInFieldsVisible);
    }

    private static Adw.ActionRow RedirectRow(string address)
    {
        var row = WinoSettingsRows.Action(address);
        row.AddCssClass("monospace");
        row.SetTitleSelectable(true);
        return row;
    }

    private Adw.Spinner SigningInSpinner() => _form.When(OnboardingUi.Spinner(), s => s.IsOAuthSigningIn);

    /// <summary>A rounded, tinted card with a leading glyph, as the Windows secure sign-in and signed-in cards.</summary>
    private static Gtk.Widget Card(WinoIconGlyph glyph, string tone, Gtk.Widget content)
    {
        var icon = WinoIcons.Label(glyph, 20);
        icon.SetValign(Gtk.Align.Start);
        content.SetHexpand(true);
        var card = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
        card.AddCssClass("card");
        card.AddCssClass(tone);
        card.Append(icon);
        card.Append(content);
        return card;
    }
}
