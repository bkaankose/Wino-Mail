using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views.Settings;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Onboarding;

/// <summary>
/// Custom IMAP / SMTP (and CalDAV / CardDAV) servers (Windows ImapCalDavSettingsPage, macOS controller of the same
/// name). Adding an account it is a guided flow, the apps (when they were not chosen yet), sign in, then the discovered servers; editing an account it is the
/// account's "Sign-in and servers" page in the Settings window: the sign-in and every server section. The page re-reads the
/// ViewModel once per main-loop turn (<see cref="OnboardingForm{T}"/>); fields, switches and drop-downs are two-way.
/// </summary>
internal sealed partial class ImapCalDavSettingsView : WinoView<ImapCalDavSettingsPageViewModel>, ISettingsPageTitleSource
{
    private readonly Adw.ToolbarView _root;
    private readonly Adw.HeaderBar _header;
    private readonly OnboardingForm<ImapCalDavSettingsPageViewModel> _form;
    private readonly Dictionary<string, Gtk.Editable> _fields = [];
    private bool _wasEditMode;

    public ImapCalDavSettingsView(ImapCalDavSettingsPageViewModel viewModel, WinoViewServices services) : base(viewModel, services)
    {
        var vm = ViewModel;
        _form = new OnboardingForm<ImapCalDavSettingsPageViewModel>(vm, Bindings, Dispatcher, ReportError);
        _form.Track(vm.Capabilities);
        _form.TrackCollection(vm.AvailableAuthenticationMethodDisplayNames);
        _form.Refreshed += () =>
        {
            if (_wasEditMode == ViewModel.IsEditMode) return;
            _wasEditMode = ViewModel.IsEditMode;
            PageTitleChanged?.Invoke(this, EventArgs.Empty);
        };

        var pageError = new WinoInfoBar(WinoInfoBarSeverity.Error) { IsClosable = true };
        Bindings.Own(pageError);
        pageError.Closed += (_, _) => _form.Run(ViewModel.HidePageErrorCommand);
        _form.Apply(() =>
        {
            pageError.Title = ViewModel.PageInfoBarTitle;
            pageError.Message = ViewModel.PageInfoBarMessage;
            if (ViewModel.IsPageInfoBarOpen != pageError.IsOpen) pageError.IsOpen = ViewModel.IsPageInfoBarOpen;
        });

        var oauthNotice = new WinoInfoBar(WinoInfoBarSeverity.Informational) { IsClosable = true };
        Bindings.Own(oauthNotice);
        oauthNotice.Closed += (_, _) => _form.Run(ViewModel.HideOAuthInfoBarCommand);
        _form.Apply(() =>
        {
            oauthNotice.Message = ViewModel.OAuthInfoBarMessage;
            oauthNotice.Severity = OnboardingUi.ToSeverity(ViewModel.OAuthInfoBarType);
            if (ViewModel.IsOAuthInfoBarOpen != oauthNotice.IsOpen) oauthNotice.IsOpen = ViewModel.IsOAuthInfoBarOpen;
        });

        var pop3 = new WinoInfoBar(WinoInfoBarSeverity.Informational, Translator.POP3Setup_LimitationsTitle, Translator.POP3Setup_LimitationsMessage);
        Bindings.Own(pop3);
        _form.When(pop3.Root, s => s.IsPop3);

        var content = OnboardingUi.Column(16, BuildPageHeader(), pageError.Root, oauthNotice.Root, pop3.Root,
            BuildProtocolStep(), BuildFeaturesStep(), BuildSignInStep(), BuildPendingApp(), BuildEditSignIn(), BuildServers());
        content.SetMarginTop(24);
        content.SetMarginBottom(24);
        content.SetMarginStart(16);
        content.SetMarginEnd(16);
        var clamp = Adw.Clamp.New();
        clamp.SetMaximumSize(640);
        clamp.SetTighteningThreshold(640);
        clamp.SetChild(content);
        var scroller = Gtk.ScrolledWindow.New();
        scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroller.SetVexpand(true);
        scroller.SetChild(clamp);

        _header = Adw.HeaderBar.New();
        _header.SetShowTitle(false);
        _root = Adw.ToolbarView.New();
        _root.AddTopBar(_header);
        _root.SetContent(scroller);
        _root.AddBottomBar(BuildFooter());
#if DEBUG
        Bindings.Own(DebugTrack());
#endif
    }

    public override Gtk.Widget Root => _root;

    public override bool HasPendingWork => base.HasPendingWork || ViewModel.IsBusy;

    /// <summary>Editing in the Settings window, the window names the page; adding an account, the page shows its own title.</summary>
    public string? PageTitle => ViewModel.IsEditMode ? Translator.ImapCalDavSettingsPage_TitleEdit : null;

    public string? PageDescription => null;

    public event EventHandler? PageTitleChanged;

    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        _header.SetVisible(!OnboardingUi.HasWindowHeader(_root));
        ViewModel.IsSecureSignInSupportedByHost = true;
        ViewModel.IsFeaturesStepSupportedByHost = true;
        await ViewModel.InitializeAsync(mode, parameter);
        if (IsReleased) return;
        _form.Refresh();
    }

    private T Field<T>(string key, T editable) where T : Gtk.Editable
    {
        _fields[key] = editable;
        return editable;
    }

    private Gtk.Widget BuildPageHeader()
    {
        var steps = new SetupStepIndicator();
        _form.When(steps.Root, s => s.IsCreateMode);
        _form.Apply(() => steps.SetCurrent(ViewModel.CurrentSetupStep));
        var title = OnboardingUi.Title();
        title.AccessibleRole = Gtk.AccessibleRole.Heading;
        _form.Text(title, s => s.IsSignInStepVisible ? s.SignInTitle
            : s.IsEditMode ? Translator.ImapCalDavSettingsPage_TitleEdit : Translator.ImapCalDavSettingsPage_TitleCreate);
        var description = _form.Optional(OnboardingUi.Subtitle(), s => s.IsSignInStepVisible ? s.SignInDescription : string.Empty);
        return _form.When(OnboardingUi.Column(12, steps.Root, title, description), s => !s.IsEditMode);
    }

    private Gtk.Widget BuildFooter()
    {
        var vm = ViewModel;
        var spinner = OnboardingUi.Spinner();
        var busyText = _form.Text(OnboardingUi.Body(), s => s.BusyText);
        busyText.AddCssClass("dim-label");
        busyText.SetEllipsize(Pango.EllipsizeMode.End);
        busyText.SetWrap(false);
        var busy = _form.When(Gtk.Box.New(Gtk.Orientation.Horizontal, 8), s => s.IsBusy);
        busy.Append(spinner);
        busy.Append(busyText);
        busy.SetHexpand(true);
        var spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        spacer.SetHexpand(true);

        var back = _form.When(_form.Button(Translator.Buttons_Back, vm.BackCommand), s => s.IsCreateMode);
        var cancel = _form.When(_form.Button(Translator.Buttons_Cancel, vm.CancelCommand), s => s.IsEditMode);
        var primary = _form.Button(Translator.ProviderSelection_ContinueButton, vm.PrimaryActionCommand, suggested: true);
        primary.SetSizeRequest(110, -1);
        _form.When(primary, s => s.IsPrimaryActionVisible);
        _form.Apply(() =>
        {
            if (primary.GetLabel() == ViewModel.PrimaryActionText) return;
            primary.SetLabel(ViewModel.PrimaryActionText);
            WinoA11y.Rename(primary, ViewModel.PrimaryActionText);
        });

        var footer = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        footer.AddCssClass("toolbar");
        footer.SetMarginTop(6);
        footer.SetMarginBottom(6);
        footer.SetMarginStart(12);
        footer.SetMarginEnd(12);
        footer.Append(busy);
        footer.Append(spacer);
        footer.Append(back);
        footer.Append(cancel);
        footer.Append(primary);
        return footer;
    }

    /// <summary>
    /// Adding an account, first step. The address comes first: discovery decides whether the server offers secure sign-in
    /// before a password is asked for, then the method stage shows the browser sign-in or the password.
    /// </summary>
    private Gtk.Widget BuildSignInStep()
        => _form.When(OnboardingUi.Column(16, BuildIdentityStage(), BuildMethodStage()), s => s.IsSignInStepVisible);

    private Gtk.Widget BuildIdentityStage()
    {
        var vm = ViewModel;
        var group = OnboardingUi.Group();
        AddIdentityRows(group, "");
        var mailServer = Field("mail-server", _form.Entry(Translator.ImapSetup_ServerAddressOptional, nameof(vm.MailServerAddress), s => s.MailServerAddress, (s, v) => s.MailServerAddress = v, Gtk.InputPurpose.Url));
        group.Add(_form.When(mailServer, s => s.IsSignInMailServerAddressVisible));
        // Without mail there is nothing to discover from reliably, so the CalDAV address can be typed here.
        var davServer = Field("dav-server", _form.Entry(Translator.ImapSetup_ServerAddressOptional, nameof(vm.CalDavServiceUrl), s => s.CalDavServiceUrl, (s, v) => s.CalDavServiceUrl = v, Gtk.InputPurpose.Url));
        group.Add(_form.When(davServer, s => s.IsSignInServerAddressVisible));
        foreach (var row in new[] { mailServer, davServer }) _form.OnEnter(row, vm.PrimaryActionCommand);

        var exchange = _form.When(_form.Link(Translator.ExchangeSetup_OpenFromImap, vm.OpenExchangeSetupCommand), s => s.IsCreateMode);
        return _form.When(OnboardingUi.Column(8, group, exchange), s => s.IsSignInIdentityStageVisible);
    }

    /// <summary>The sender name and the address; the edit sign-in group shows the same rows.</summary>
    private void AddIdentityRows(Adw.PreferencesGroup group, string prefix)
    {
        var vm = ViewModel;
        var name = Field(prefix + "name", _form.Entry(Translator.ImapSetup_YourName, nameof(vm.DisplayName), s => s.DisplayName, (s, v) => s.DisplayName = v));
        WinoA11y.Description(name, Translator.ImapSetup_YourNameDescription);
        group.Add(_form.When(name, s => s.IsSignInSenderNameVisible));
        var address = Field(prefix + "address", _form.Entry(Translator.IMAPSetupDialog_MailAddress, nameof(vm.EmailAddress), s => s.EmailAddress, (s, v) => s.EmailAddress = v, Gtk.InputPurpose.Email));
        _form.Apply(() => SetTitle(address, ViewModel.SignInAddressHeader));
        group.Add(address);
        _form.OnEnter(name, vm.PrimaryActionCommand);
        _form.OnEnter(address, vm.PrimaryActionCommand);
    }

    /// <summary>The account password with the provider's app-password help; <paramref name="visible"/> picks the stage it belongs to.</summary>
    private Gtk.Widget BuildPasswordRows(string key, Func<ImapCalDavSettingsPageViewModel, bool> visible)
    {
        var vm = ViewModel;
        var password = Field(key, _form.Password(Translator.IMAPSetupDialog_Password, nameof(vm.Password), s => s.Password, (s, v) => s.Password = v));
        _form.OnEnter(password, vm.PrimaryActionCommand);
        var group = OnboardingUi.Group();
        group.Add(password);
        var help = _form.Optional(OnboardingUi.Caption(), s => s.AppPasswordHelpText);
        var link = _form.When(_form.Link(Translator.ImapSetup_AppPasswordHelpLink, vm.OpenAppPasswordHelpCommand), s => s.HasAppPasswordHelpLink);
        return _form.When(OnboardingUi.Column(6, group, help, link), visible);
    }

    /// <summary>How to sign in, once discovery has run: the browser sign-in the server offers, or the password.</summary>
    private Gtk.Widget BuildMethodStage()
    {
        var vm = ViewModel;
        var summary = _form.Text(OnboardingUi.Body(), s => s.SignInAddressSummary);
        summary.AddCssClass("dim-label");
        summary.SetEllipsize(Pango.EllipsizeMode.Middle);
        summary.SetWrap(false);
        summary.SetHexpand(true);
        var addressRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        addressRow.Append(summary);
        addressRow.Append(_form.Link(Translator.ImapSetup_ChangeAddress, vm.ChangeAddressCommand));

        var useSecure = _form.When(_form.Link(Translator.ImapSetup_UseSecureSignInInstead, vm.UseSecureSignInInsteadCommand), s => s.IsUseSecureSignInVisible);
        var organization = _form.When(_form.Link(Translator.ImapSetup_OrganizationSignIn, vm.ToggleOrganizationSignInCommand), s => s.IsOrganizationSignInOptionVisible);
        var passwordMethod = _form.When(OnboardingUi.Column(8, BuildPasswordRows("password", static _ => true), useSecure, organization), s => s.IsPasswordMethodVisible);

        return _form.When(OnboardingUi.Column(12, addressRow, BuildOAuthChoice(), BuildOAuthSignedIn(), passwordMethod, BuildOrganizationSignIn()),
            s => s.IsSignInMethodStageVisible);
    }

    private static void SetTitle(Adw.PreferencesRow row, string? title)
    {
        var value = title ?? string.Empty;
        if (row.GetTitle() == value) return;
        row.SetTitle(value);
        WinoA11y.Rename(row, value);
    }
}
