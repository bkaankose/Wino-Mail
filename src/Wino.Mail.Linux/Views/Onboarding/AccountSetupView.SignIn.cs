using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// The sign-in step (IMAP signs in here, by browser or password, then goes on to the review; Exchange with a password
/// signs in here after its review) and the progress step (adding, added, failed).
/// </summary>
internal sealed partial class AccountSetupView
{
    private Gtk.Button _browserSignIn = null!;
    private Adw.PasswordEntryRow _password = null!;
    private Adw.PasswordEntryRow _exchangePassword = null!;
    private readonly AccountSetupStepList _stepList = new();
    private Gtk.Box _resultRows = null!;
    private List<AccountSetupResultRow>? _shownResultRows;

    private Gtk.Widget BuildSignInStep()
    {
        var vm = ViewModel;
        var issuer = _form.Check(string.Empty, nameof(vm.IsIssuerConfirmed), s => s.IsIssuerConfirmed, (s, v) => s.IsIssuerConfirmed = v);
        _form.Apply(() =>
        {
            if (issuer.GetLabel() != ViewModel.IssuerConfirmationText) issuer.SetLabel(ViewModel.IssuerConfirmationText);
        });
        var issuerCaption = OnboardingUi.Caption(Translator.ImapSetup_OAuthConfirmIssuerDescription);
        issuerCaption.SetMarginStart(28);
        var issuerGroup = _form.When(OnboardingUi.Column(2, issuer, issuerCaption), s => s.IsIssuerConfirmationVisible);

        var browserGlyph = new WinoIcon(WinoIconGlyph.Globe, 16, WinoIconMode.Colorful);
        var browserText = Gtk.Label.New(string.Empty);
        var browserContent = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        browserContent.Append(browserGlyph.Root);
        browserContent.Append(browserText);
        _browserSignIn = Gtk.Button.New();
        _browserSignIn.SetChild(browserContent);
        _browserSignIn.AddCssClass("suggested-action");
        _browserSignIn.AddCssClass("pill");
        _form.Command(_browserSignIn, vm.SignInWithBrowserCommand);
        _form.Apply(() =>
        {
            var text = ViewModel.BrowserSignInButtonText ?? string.Empty;
            if (browserText.GetText() != text)
            {
                browserText.SetText(text);
                WinoA11y.Rename(_browserSignIn, text);
            }
            browserGlyph.SetIcon(BrowserSignInMark(ViewModel.RouteSpecialImapProvider));
        });
        var browserRow = _form.When(OnboardingUi.Centered(_browserSignIn), s => s.IsBrowserSignInButtonVisible);

        var sendAs = Field("sender", _form.Entry(Translator.AccountSetup_SendAsHeader, nameof(vm.SenderName), s => s.SenderName, (s, v) => s.SenderName = v));
        var sendAsGroup = OnboardingUi.Group(null, Translator.AccountSetup_SendAsDescription);
        sendAsGroup.Add(sendAs);
        _form.When(sendAsGroup, s => s.IsSendAsVisible);

        return OnboardingUi.Column(12, issuerGroup, browserRow, BuildPasswordPanel(), BuildExchangePassword(), sendAsGroup,
            BuildConnectionStatus(), BuildFailureBar(offersAnotherOption: true));
    }

    /// <summary>The provider's mark on the browser sign-in button when Wino has one, otherwise a globe.</summary>
    private static WinoIconGlyph BrowserSignInMark(SpecialImapProvider provider)
    {
        var glyph = MailProviderGlyphs.GetGlyph(MailProviderType.IMAP4, provider);
        return glyph is WinoIconGlyph.IMAP or WinoIconGlyph.None ? WinoIconGlyph.Globe : glyph;
    }

    private Gtk.Widget BuildPasswordPanel()
    {
        var vm = ViewModel;
        var hint = _form.Text(OnboardingUi.Caption(), s => s.SignInCaption);
        var help = _form.Link(string.Empty, vm.OpenHelpLinkCommand, () => ViewModel.HelpUrl);
        _form.Apply(() => OnboardingUi.SetLabel(help, ViewModel.HelpLinkText));
        _form.When(help, s => s.IsHelpLinkVisible);
        var hintGroup = _form.When(OnboardingUi.Column(2, hint, help), s => s.IsSetupHintCaptionVisible);

        var region = _form.Combo(Translator.ProviderSelection_RegionHeader, s => s.Regions.Select(item => item.DisplayName).ToArray(), nameof(vm.SelectedRegion),
            s => s.SelectedRegion is null ? -1 : s.Regions.IndexOf(s.SelectedRegion),
            (s, index) => { if (index >= 0 && index < s.Regions.Count) s.SelectedRegion = s.Regions[index]; });
        _form.Apply(() =>
        {
            var description = ViewModel.RegionDescription ?? string.Empty;
            if (region.GetSubtitle() != description) region.SetSubtitle(description);
        });
        var regionGroup = OnboardingUi.Group();
        regionGroup.Add(region);
        _form.When(regionGroup, s => s.IsRegionVisible);

        var login = Field("login", _form.Entry(string.Empty, nameof(vm.LoginAddress), s => s.LoginAddress, (s, v) => s.LoginAddress = v, Gtk.InputPurpose.Email));
        _form.Apply(() => SetTitle(login, ViewModel.LoginAddressHeader));
        var loginGroup = OnboardingUi.Group();
        loginGroup.Add(login);
        var loginDescription = _form.Optional(OnboardingUi.Caption(), s => s.LoginAddressDescription);
        var loginColumn = _form.When(OnboardingUi.Column(4, loginGroup, loginDescription), s => s.IsLoginAddressVisible);

        var userName = Field("username", _form.Entry(Translator.AccountSetup_UserNameHeader, nameof(vm.ImapUserName), s => s.ImapUserName, (s, v) => s.ImapUserName = v));
        var userNameGroup = OnboardingUi.Group(null, Translator.AccountSetup_UserNameDescription);
        userNameGroup.Add(userName);
        _form.When(userNameGroup, s => s.IsImapUserNameVisible);

        _password = Field("password", _form.Password(string.Empty, nameof(vm.Password), s => s.Password, (s, v) => s.Password = v));
        _form.Apply(() => SetTitle(_password, ViewModel.PasswordHeader));
        _form.Enabled(_password, s => !s.IsConnectionStatusBusy);
        _form.OnEnter(_password, vm.PrimaryCommand);
        var passwordGroup = OnboardingUi.Group();
        passwordGroup.Add(_password);

        var otherUserName = _form.When(_form.Link(Translator.AccountSetup_UseDifferentUserName, vm.ShowImapUserNameCommand), s => s.IsImapUserNameLinkVisible);

        return _form.When(OnboardingUi.Column(12, hintGroup, regionGroup, loginColumn, userNameGroup, passwordGroup, otherUserName), s => s.IsPasswordPanelVisible);
    }

    /// <summary>Exchange without modern auth: the sign-in name when it is not the address, then the password.</summary>
    private Gtk.Widget BuildExchangePassword()
    {
        var vm = ViewModel;
        var caption = _form.Text(OnboardingUi.Caption(), s => s.ExchangePasswordCaption);
        var userName = Field("exchange-user", _form.Entry(Translator.ExchangeSetup_UserNameHeader, nameof(vm.ExchangeUserName), s => s.ExchangeUserName, (s, v) => s.ExchangeUserName = v));
        _form.Enabled(userName, s => !s.IsConnectionStatusBusy);
        _exchangePassword = Field("exchange-password", _form.Password(Translator.ProviderSelection_PasswordHeader_Account, nameof(vm.Password), s => s.Password, (s, v) => s.Password = v));
        _form.Enabled(_exchangePassword, s => !s.IsConnectionStatusBusy);
        _form.OnEnter(_exchangePassword, vm.PrimaryCommand);
        var group = OnboardingUi.Group(null, Translator.ExchangeSetup_UserNameDescription);
        group.Add(userName);
        group.Add(_exchangePassword);
        return _form.When(OnboardingUi.Column(8, caption, group), s => s.IsExchangePasswordVisible);
    }

    /// <summary>One status line: waiting or checking, then signed in or all connections work.</summary>
    private Gtk.Widget BuildConnectionStatus()
    {
        var spinner = _form.When(OnboardingUi.Spinner(), s => s.IsConnectionStatusBusy);
        var check = WinoIcons.Label(WinoIconGlyph.CheckmarkCircle, 16);
        check.AddCssClass("success");
        _form.When(check, s => s.IsConnectionStatusConnected);
        var text = _form.Text(OnboardingUi.Body(), s => s.ConnectionStatusText);
        return _form.When(OnboardingUi.Centered(spinner, check, text), s => s.IsConnectionStatusVisible);
    }

    private Gtk.Widget? SignInFocusTarget()
        => new Gtk.Widget?[] { _browserSignIn, _password, _exchangePassword }.FirstOrDefault(widget => widget is not null && widget.IsDrawable());

    /// <summary>Adding, Added and Failed: the setup steps, then the result or the failure with its actions.</summary>
    private Gtk.Widget BuildProgressStep()
    {
        var vm = ViewModel;
        Bindings.Own(_stepList.Bind(vm.Steps, Dispatcher, ReportError));

        var added = new WinoInfoBar(WinoInfoBarSeverity.Success);
        Bindings.Own(added);
        _form.Apply(() => added.Message = ViewModel.AddedMessage);
        _form.When(added.Root, s => s.IsAddedVisible);

        _resultRows = OnboardingUi.Column(0);
        _resultRows.SetHalign(Gtk.Align.Center);
        _form.Apply(RenderResultRows);
        _form.When(_resultRows, s => s.IsAddedVisible);

        var failed = new WinoInfoBar(WinoInfoBarSeverity.Error, null, Translator.AccountSetup_FailureMessage);
        Bindings.Own(failed);
        failed.Content = OnboardingUi.Buttons(
            _form.Button(Translator.AccountSetup_TryAgainButton, vm.TryAgainCommand, suggested: true),
            _form.Button(Translator.AccountSetup_GoBackButton, vm.GoBackFromFailureCommand),
            _form.When(_form.Link(Translator.AccountSetup_ShowDetails, vm.ShowSetupFailureDetailsCommand), s => s.HasSetupFailureDetails));
        _form.When(failed.Root, s => s.IsSetupFailedVisible);

        return OnboardingUi.Column(12, _stepList.Root, added.Root, _resultRows, failed.Root);
    }

    private void RenderResultRows()
    {
        var rows = ViewModel.ResultRows;
        if (ReferenceEquals(rows, _shownResultRows)) return;
        _shownResultRows = rows;
        while (_resultRows.GetFirstChild() is { } child) _resultRows.Remove(child);
        foreach (var row in rows) _resultRows.Append(AccountSetupResultRowView.Create(row));
    }

    private static void SetTitle(Adw.PreferencesRow row, string? title)
    {
        var value = title ?? string.Empty;
        if (row.GetTitle() == value) return;
        row.SetTitle(value);
        WinoA11y.Rename(row, value);
    }
}
