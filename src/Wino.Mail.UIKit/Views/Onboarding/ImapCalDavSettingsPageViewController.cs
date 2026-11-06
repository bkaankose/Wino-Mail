using System.Collections.Specialized;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Onboarding;

/// <summary>
/// Custom IMAP / SMTP (and CalDAV / CardDAV) server page (Windows ImapCalDavSettingsPage, Mac twin). Adding an account
/// it is a guided flow (the address, then the secure sign-in or password the server offers, then the discovered
/// servers); editing an account it shows the features, the sign-in and every server section. Server sections are
/// collapsible settings expanders.
/// </summary>
public sealed class ImapCalDavSettingsPageViewController(ImapCalDavSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<ImapCalDavSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    private FormBinder<ImapCalDavSettingsPageViewModel>? _form;

    private FormBinder<ImapCalDavSettingsPageViewModel> Form => _form ??= new FormBinder<ImapCalDavSettingsPageViewModel>(ViewModel, Bindings, Dispatcher, ReportError);

    public override void LoadView()
    {
        var vm = ViewModel;
        var content = WinoLayout.VStack(WinoStyle.Space4);
        content.Alignment = UIStackViewAlignment.Fill;

        var steps = new SetupStepIndicator();
        Form.Visible(steps, nameof(vm.IsCreateMode), s => s.IsCreateMode);
        Form.Bind(nameof(vm.CurrentSetupStep), s => s.CurrentSetupStep, steps.SetCurrent);

        var pageError = new WinoInfoBar(WinoInfoBarSeverity.Error) { IsClosable = true };
        Form.Bind(nameof(vm.PageInfoBarTitle), s => s.PageInfoBarTitle, text => pageError.Title = text);
        Form.Bind(nameof(vm.PageInfoBarMessage), s => s.PageInfoBarMessage, text => pageError.Message = text);
        Form.Visible(pageError, nameof(vm.IsPageInfoBarOpen), s => s.IsPageInfoBarOpen);
        EventHandler<WinoInfoBarClosedEventArgs> closeError = (_, _) => _ = RunAsync(() => vm.HidePageErrorCommand.ExecuteAsync(null));
        pageError.Closed += closeError;
        Bindings.Own(new ActionDisposable(() => pageError.Closed -= closeError));
        content.AddArrangedSubview(pageError);

        var oauthNotice = new WinoInfoBar(WinoInfoBarSeverity.Informational) { IsClosable = true };
        Form.Bind(nameof(vm.OAuthInfoBarMessage), s => s.OAuthInfoBarMessage, text => oauthNotice.Message = text);
        Form.Bind(nameof(vm.OAuthInfoBarType), s => s.OAuthInfoBarType, type => oauthNotice.Severity = type switch
        {
            InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
            InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
            InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
            _ => WinoInfoBarSeverity.Informational
        });
        Form.Visible(oauthNotice, nameof(vm.IsOAuthInfoBarOpen), s => s.IsOAuthInfoBarOpen);
        EventHandler<WinoInfoBarClosedEventArgs> closeOAuthNotice = (_, _) => vm.HideOAuthInfoBarCommand.Execute(null);
        oauthNotice.Closed += closeOAuthNotice;
        Bindings.Own(new ActionDisposable(() => oauthNotice.Closed -= closeOAuthNotice));
        content.AddArrangedSubview(oauthNotice);

        var pop3 = new WinoInfoBar(WinoInfoBarSeverity.Informational, Translator.POP3Setup_LimitationsTitle, Translator.POP3Setup_LimitationsMessage);
        Form.Visible(pop3, nameof(vm.IsPop3), s => s.IsPop3);
        content.AddArrangedSubview(pop3);

        content.AddArrangedSubview(BuildSignInStep());
        content.AddArrangedSubview(BuildEditFeatures());
        content.AddArrangedSubview(BuildEditSignIn());
        content.AddArrangedSubview(BuildServers());

        var title = new UILabel();
        var description = new UILabel();
        void UpdateHeader()
        {
            title.Text = vm.IsSignInStepVisible ? vm.SignInTitle
                : vm.IsEditMode ? Translator.ImapCalDavSettingsPage_TitleEdit : Translator.ImapCalDavSettingsPage_TitleCreate;
            description.Text = vm.IsSignInStepVisible ? vm.SignInDescription : string.Empty;
            description.Hidden = string.IsNullOrEmpty(description.Text);
        }
        foreach (var property in new[] { nameof(vm.IsSignInStepVisible), nameof(vm.SignInTitle), nameof(vm.SignInDescription), nameof(vm.IsEditMode) })
            Form.Bind(property, _ => 0, _ => UpdateHeader());
        var header = WinoLayout.VStack(WinoStyle.Space4, steps, OnboardingPageView.Header(null, title, description));
        header.Alignment = UIStackViewAlignment.Fill;

        var spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true };
        var busyText = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText);
        var busy = WinoLayout.HStack(WinoStyle.Space2, spinner, busyText);
        Form.Label(busyText, nameof(vm.BusyText), s => s.BusyText);
        Form.Bind(nameof(vm.IsBusy), s => s.IsBusy, isBusy =>
        {
            busy.Hidden = !isBusy;
            if (isBusy) spinner.StartAnimating(); else spinner.StopAnimating();
        });

        var back = Form.Command(Translator.Buttons_Back, vm.BackCommand);
        Form.Visible(back, nameof(vm.IsCreateMode), s => s.IsCreateMode);
        var cancel = Form.Command(Translator.Buttons_Cancel, vm.CancelCommand);
        Form.Visible(cancel, nameof(vm.IsEditMode), s => s.IsEditMode);
        var primary = Form.Command(Translator.ProviderSelection_ContinueButton, vm.PrimaryActionCommand, primary: true);
        Form.Bind(nameof(vm.PrimaryActionText), s => s.PrimaryActionText, text => SetTitle(primary, text));
        // Until the user signs in on the secure sign-in card there is nothing to continue with.
        VisibleWhen(primary, s => s.IsPrimaryActionVisible, nameof(vm.IsPrimaryActionVisible));

        View = new OnboardingPageView(header, content, busy, [back, cancel], primary, 640);
    }

    /// <summary>
    /// Adding an account, first step. The address comes first: discovery decides whether the server offers secure
    /// sign-in before a password is asked for, then the method stage shows the browser sign-in or the password.
    /// </summary>
    private UIView BuildSignInStep()
    {
        var vm = ViewModel;
        var group = OnboardingPageView.Group(null, Column(BuildIdentityStage(), BuildMethodStage()));
        Form.Visible(group, nameof(vm.IsSignInStepVisible), s => s.IsSignInStepVisible);
        return group;
    }

    private UIView BuildIdentityStage()
    {
        var vm = ViewModel;
        var grid = new FormGrid();
        AddIdentityRows(grid);

        var mailServer = Form.Text(nameof(vm.MailServerAddress), s => s.MailServerAddress, (s, v) => s.MailServerAddress = v,
            Translator.ImapSetup_ServerAddressPlaceholder, Translator.ImapSetup_ServerAddressOptional);
        mailServer.KeyboardType = UIKeyboardType.Url;
        var mailServerRow = grid.Row(Translator.ImapSetup_ServerAddressOptional, mailServer);
        Form.Visible(mailServerRow, nameof(vm.IsSignInMailServerAddressVisible), s => s.IsSignInMailServerAddressVisible);

        // Without mail there is nothing to discover from reliably, so the CalDAV address can be typed here.
        var server = Form.Text(nameof(vm.CalDavServiceUrl), s => s.CalDavServiceUrl, (s, v) => s.CalDavServiceUrl = v,
            Translator.ImapSetup_ServerAddressPlaceholder, Translator.ImapSetup_ServerAddressOptional);
        server.KeyboardType = UIKeyboardType.Url;
        var serverRow = grid.Row(Translator.ImapSetup_ServerAddressOptional, server);
        Form.Visible(serverRow, nameof(vm.IsSignInServerAddressVisible), s => s.IsSignInServerAddressVisible);

        var exchange = Form.Link(Translator.ExchangeSetup_OpenFromImap, vm.OpenExchangeSetupCommand);
        exchange.TitleLabel.Lines = 0;
        Form.Visible(exchange, nameof(vm.IsCreateMode), s => s.IsCreateMode);

        var stage = Column(grid, exchange);
        VisibleWhen(stage, s => s.IsSignInIdentityStageVisible, nameof(vm.IsSignInIdentityStageVisible), nameof(vm.SignInStage));
        return stage;
    }

    private void AddIdentityRows(FormGrid grid)
    {
        var vm = ViewModel;
        var name = Form.Text(nameof(vm.DisplayName), s => s.DisplayName, (s, v) => s.DisplayName = v,
            Translator.IMAPSetupDialog_DisplayNamePlaceholder, Translator.ImapSetup_YourName);
        name.AutocapitalizationType = UITextAutocapitalizationType.Words;
        var nameRow = grid.Row(Translator.ImapSetup_YourName, name);
        var nameHelpRow = grid.Row((string?)null, FormGrid.Help(Translator.ImapSetup_YourNameDescription));
        Form.Visible(nameRow, nameof(vm.IsSignInSenderNameVisible), s => s.IsSignInSenderNameVisible);
        Form.Visible(nameHelpRow, nameof(vm.IsSignInSenderNameVisible), s => s.IsSignInSenderNameVisible);

        var addressLabel = FormGrid.FieldLabel(Translator.IMAPSetupDialog_MailAddress);
        Form.Label(addressLabel, nameof(vm.SignInAddressHeader), s => s.SignInAddressHeader);
        var address = Form.Text(nameof(vm.EmailAddress), s => s.EmailAddress, (s, v) => s.EmailAddress = v, Translator.IMAPSetupDialog_MailAddressPlaceholder);
        address.KeyboardType = UIKeyboardType.EmailAddress;
        address.TextContentType = UITextContentType.EmailAddress;
        Form.Bind(nameof(vm.SignInAddressHeader), s => s.SignInAddressHeader, text => address.AccessibilityLabel = text);
        grid.Row(addressLabel, address);
    }

    private void AddPasswordRows(FormGrid grid, string visibleProperty, Func<ImapCalDavSettingsPageViewModel, bool> visible)
    {
        var vm = ViewModel;
        var password = Form.Secure(nameof(vm.Password), s => s.Password, (s, v) => s.Password = v, Translator.IMAPSetupDialog_Password);
        var passwordRow = grid.Row(Translator.IMAPSetupDialog_Password, password);
        var help = FormGrid.Help();
        Form.Label(help, nameof(vm.AppPasswordHelpText), s => s.AppPasswordHelpText);
        Form.Visible(help, nameof(vm.AppPasswordHelpText), s => !string.IsNullOrWhiteSpace(s.AppPasswordHelpText));
        var link = Form.Link(Translator.ImapSetup_AppPasswordHelpLink, vm.OpenAppPasswordHelpCommand);
        Form.Visible(link, nameof(vm.HasAppPasswordHelpLink), s => s.HasAppPasswordHelpLink);
        var helpStack = WinoLayout.VStack(4, help, link);
        helpStack.Alignment = UIStackViewAlignment.Leading;
        var helpRow = grid.Row((string?)null, helpStack);
        Form.Visible(passwordRow, visibleProperty, visible);
        Form.Visible(helpRow, visibleProperty, visible);
    }

    /// <summary>How to sign in, once discovery has run: the browser sign-in the server offers, or the password.</summary>
    private UIView BuildMethodStage()
    {
        var vm = ViewModel;

        var summary = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText);
        summary.LineBreakMode = UILineBreakMode.MiddleTruncation;
        summary.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        Form.Label(summary, nameof(vm.SignInAddressSummary), s => s.SignInAddressSummary);
        var change = Form.Link(Translator.ImapSetup_ChangeAddress, vm.ChangeAddressCommand);
        change.SetContentCompressionResistancePriority(750, UILayoutConstraintAxis.Horizontal);
        change.SetContentHuggingPriority(750, UILayoutConstraintAxis.Horizontal);
        var addressRow = WinoLayout.HStack(WinoStyle.Space2, summary, change);
        addressRow.Alignment = UIStackViewAlignment.FirstBaseline;

        var passwordGrid = new FormGrid();
        AddPasswordRows(passwordGrid, nameof(vm.IsPasswordMethodVisible), s => s.IsPasswordMethodVisible);
        var useSecure = Form.Link(Translator.ImapSetup_UseSecureSignInInstead, vm.UseSecureSignInInsteadCommand);
        VisibleWhen(useSecure, s => s.IsUseSecureSignInVisible, nameof(vm.IsUseSecureSignInVisible));
        var organization = Form.Link(Translator.ImapSetup_OrganizationSignIn, vm.ToggleOrganizationSignInCommand);
        VisibleWhen(organization, s => s.IsOrganizationSignInOptionVisible, nameof(vm.IsOrganizationSignInOptionVisible));
        var passwordMethod = Column(passwordGrid, useSecure, organization);
        VisibleWhen(passwordMethod, s => s.IsPasswordMethodVisible, nameof(vm.IsPasswordMethodVisible));

        var stage = Column(addressRow, BuildOAuthChoice(), BuildOAuthSignedIn(), passwordMethod, BuildOrganizationSignIn());
        VisibleWhen(stage, s => s.IsSignInMethodStageVisible, nameof(vm.IsSignInMethodStageVisible), nameof(vm.SignInStage));
        return stage;
    }

    /// <summary>The server offers secure sign-in: the web authentication sheet opens only when the user taps.</summary>
    private UIView BuildOAuthChoice()
    {
        var vm = ViewModel;
        var title = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        Form.Label(title, nameof(vm.SecureSignInTitle), s => s.SecureSignInTitle);
        var description = WinoStyle.Label(Translator.ImapSetup_SecureSignInDescription, WinoStyle.Description, WinoStyle.SecondaryText, 0);

        var confirmation = Column(IssuerConfirmationSwitch(),
            WinoStyle.Label(Translator.ImapSetup_OAuthConfirmIssuerDescription, WinoStyle.Description, WinoStyle.SecondaryText, 0));
        VisibleWhen(confirmation, s => s.IsIssuerConfirmationVisible, nameof(vm.IsIssuerConfirmationVisible));

        var signIn = Form.Command(string.Empty, vm.SignInWithOAuthCommand, primary: true);
        signIn.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        Form.Bind(nameof(vm.OAuthSignInButtonText), s => s.OAuthSignInButtonText, text => SetTitle(signIn, text));
        var issuer = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        Form.Label(issuer, nameof(vm.OAuthIssuerHost), s => s.OAuthIssuerHost);
        var buttonRow = WinoLayout.HStack(WinoStyle.Space3, signIn, SigningInSpinner(), WinoLayout.Spacer());
        buttonRow.Alignment = UIStackViewAlignment.Center;

        var card = Card(WinoIconGlyph.LockClosed, WinoStyle.Accent, Column(title, description, confirmation, buttonRow, issuer));

        var usePassword = Form.Link(Translator.ImapSetup_UsePasswordInstead, vm.UsePasswordInsteadCommand);
        var choice = Column(card, usePassword);
        VisibleWhen(choice, s => s.IsOAuthChoiceVisible, nameof(vm.IsOAuthChoiceVisible));
        return choice;
    }

    /// <summary>"I trust this sign-in page": a switch whose title names the issuer the server pointed to.</summary>
    private UIView IssuerConfirmationSwitch()
    {
        var vm = ViewModel;
        var toggle = new UISwitch { TranslatesAutoresizingMaskIntoConstraints = false };
        var binding = Bindings.Own(new PropertyBinding<ImapCalDavSettingsPageViewModel, bool>(vm, nameof(vm.IsIssuerConfirmed), s => s.IsIssuerConfirmed,
            value => toggle.On = value, Dispatcher, ReportError, (s, v) => s.IsIssuerConfirmed = v));
        EventHandler changed = (_, _) => binding.UpdateSource(toggle.On);
        toggle.ValueChanged += changed;
        Bindings.Own(new ActionDisposable(() => toggle.ValueChanged -= changed));
        var label = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText, 0);
        Form.Bind(nameof(vm.IssuerConfirmationText), s => s.IssuerConfirmationText, text =>
        {
            label.Text = text ?? string.Empty;
            toggle.AccessibilityLabel = label.Text;
        });
        var row = WinoLayout.HStack(WinoStyle.Space3, label, toggle);
        row.Alignment = UIStackViewAlignment.Center;
        return row;
    }

    /// <summary>Signed in: the session stays in memory until the account is saved.</summary>
    private UIView BuildOAuthSignedIn()
    {
        var vm = ViewModel;
        var title = WinoStyle.Label(string.Empty, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        Form.Label(title, nameof(vm.OAuthSignedInTitle), s => s.OAuthSignedInTitle);
        var description = WinoStyle.Label(string.Empty, WinoStyle.Description, WinoStyle.SecondaryText, 0);
        Form.Label(description, nameof(vm.OAuthSignedInDescription), s => s.OAuthSignedInDescription);
        var again = Form.Link(Translator.ImapSetup_OAuthSignInAgain, vm.SignInAgainCommand);
        var card = Card(WinoIconGlyph.CheckmarkCircle, WinoStyle.Success, Column(title, description, again));

        var noRefresh = new WinoInfoBar(WinoInfoBarSeverity.Warning, null, Translator.ImapSetup_OAuthNoRefreshToken);
        VisibleWhen(noRefresh, s => s.IsNoRefreshTokenWarningVisible, nameof(vm.IsNoRefreshTokenWarningVisible));
        var davScope = new WinoInfoBar(WinoInfoBarSeverity.Warning, null, Translator.ImapSetup_OAuthDavScopeSignInRequired);
        VisibleWhen(davScope, s => s.IsDavScopeSignInRequiredForSetupVisible, nameof(vm.IsDavScopeSignInRequiredForSetupVisible));

        var usePassword = Form.Link(Translator.ImapSetup_UsePasswordInstead, vm.UsePasswordInsteadCommand);
        var signedIn = Column(card, noRefresh, davScope, usePassword);
        VisibleWhen(signedIn, s => s.IsOAuthSignedInVisible, nameof(vm.IsOAuthSignedInVisible));
        return signedIn;
    }

    /// <summary>The server offers OAuth but Wino has no client for it: the organization registers one.</summary>
    private UIView BuildOrganizationSignIn()
    {
        var vm = ViewModel;
        var description = WinoStyle.Label(Translator.ImapSetup_OrganizationSignInDescription, WinoStyle.Description, WinoStyle.SecondaryText, 0);

        var grid = new FormGrid();
        var issuer = Form.Text(nameof(vm.CustomOAuthIssuer), s => s.CustomOAuthIssuer, (s, v) => s.CustomOAuthIssuer = v,
            Translator.ImapSetup_OAuthIssuerPlaceholder, Translator.ImapSetup_OAuthIssuerHeader);
        issuer.KeyboardType = UIKeyboardType.Url;
        grid.Row(Translator.ImapSetup_OAuthIssuerHeader, issuer);
        grid.Row(Translator.ImapSetup_OAuthClientIdHeader, Form.Text(nameof(vm.CustomOAuthClientId), s => s.CustomOAuthClientId,
            (s, v) => s.CustomOAuthClientId = v, null, Translator.ImapSetup_OAuthClientIdHeader));
        grid.Row(Translator.ImapSetup_OAuthScopesHeader, Form.Text(nameof(vm.CustomOAuthScopes), s => s.CustomOAuthScopes,
            (s, v) => s.CustomOAuthScopes = v, Translator.ImapSetup_OAuthScopesPlaceholder, Translator.ImapSetup_OAuthScopesHeader));

        var signIn = Form.Command(Translator.ImapSetup_OAuthSignInWithClient, vm.SignInWithOrganizationClientCommand);
        signIn.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var buttonRow = WinoLayout.HStack(WinoStyle.Space3, signIn, SigningInSpinner(), WinoLayout.Spacer());
        buttonRow.Alignment = UIStackViewAlignment.Center;

        // iOS signs in only through the custom scheme; the loopback address is listed for the organization's other devices.
        var body = Column(description, RedirectText(vm.OAuthCustomSchemeRedirectUri), RedirectText(vm.OAuthLoopbackRedirectUri), grid, buttonRow);
        var panel = new WinoSurfaceView { Fill = WinoStyle.SubtleFill, Stroke = WinoStyle.GroupStroke, CornerRadius = WinoStyle.GroupRadius };
        WinoLayout.Fill(body, panel, 14);
        VisibleWhen(panel, s => s.IsOrganizationSignInFieldsVisible, nameof(vm.IsOrganizationSignInFieldsVisible));
        return panel;
    }

    /// <summary>A redirect address the administrator copies into the client registration.</summary>
    private static UITextView RedirectText(string text)
    {
        var view = new UITextView
        {
            Text = text,
            Font = UIFont.GetMonospacedSystemFont(13, UIFontWeight.Regular),
            TextColor = WinoStyle.PrimaryText,
            BackgroundColor = UIColor.Clear,
            Editable = false,
            Selectable = true,
            ScrollEnabled = false,
            TextContainerInset = UIEdgeInsets.Zero,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        view.TextContainer.LineFragmentPadding = 0;
        return view;
    }

    /// <summary>A rounded, tinted card with a leading glyph, as the Windows secure sign-in and signed-in cards.</summary>
    private static UIView Card(WinoIconGlyph glyph, UIColor tint, UIView content)
    {
        var icon = new WinoIconView(glyph, 20, tint) { Colorful = false };
        WinoLayout.Size(icon, 22, 22);
        var row = WinoLayout.HStack(WinoStyle.Space3, icon, content);
        row.Alignment = UIStackViewAlignment.Top;
        var surface = new WinoSurfaceView { Fill = tint.ColorWithAlpha(0.08f), Stroke = tint, CornerRadius = WinoStyle.GroupRadius };
        WinoLayout.Fill(row, surface, 14);
        return surface;
    }

    private UIActivityIndicatorView SigningInSpinner()
    {
        var vm = ViewModel;
        var spinner = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { HidesWhenStopped = true, TranslatesAutoresizingMaskIntoConstraints = false };
        Form.Bind(nameof(vm.IsOAuthSigningIn), s => s.IsOAuthSigningIn, active =>
        {
            if (active) spinner.StartAnimating(); else spinner.StopAnimating();
        });
        return spinner;
    }

    /// <summary>Editing an account: what it is used for, as capability menus.</summary>
    private UIView BuildEditFeatures()
    {
        var vm = ViewModel;
        var capabilities = new FormBinder<AccountCapabilitySelection>(vm.Capabilities, Bindings, Dispatcher, ReportError);
        var grid = new FormGrid();
        foreach (var (label, control) in FormBinder<AccountCapabilitySelection>.CapabilityRows(capabilities, () => Translator.ProviderSelection_ModeImap))
            grid.Row(label, control);
        var missing = WinoStyle.Label(Translator.ProviderSelection_CapabilityValidationMessage, WinoStyle.Caption, WinoStyle.Critical, 0);
        capabilities.Visible(missing, nameof(AccountCapabilitySelection.IsSelectionMissing), s => s.IsSelectionMissing);
        grid.Row((string?)null, missing);
        var group = OnboardingPageView.Group(Translator.ImapSetup_FeaturesGroup, grid);
        Form.Visible(group, nameof(vm.IsEditMode), s => s.IsEditMode);
        return group;
    }

    /// <summary>
    /// Editing an account: the sign-in shared by every server, a password or the secure sign-in. "Sign in again" also
    /// serves accounts restored from a backup, whose tokens stay on the device that signed in.
    /// </summary>
    private UIView BuildEditSignIn()
    {
        var vm = ViewModel;
        var grid = new FormGrid();
        AddIdentityRows(grid);
        AddPasswordRows(grid, nameof(vm.IsEditPasswordSignInVisible), s => s.IsEditPasswordSignInVisible);

        var reauthenticate = Form.Command(Translator.ImapSetup_OAuthSignInAgain, vm.ReauthenticateOAuthCommand);
        reauthenticate.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        var oauthAction = WinoLayout.HStack(WinoStyle.Space3, reauthenticate, SigningInSpinner(), WinoLayout.Spacer());
        oauthAction.Alignment = UIStackViewAlignment.Center;
        var oauthDescription = FormGrid.Help();
        Form.Label(oauthDescription, nameof(vm.EditOAuthDescription), s => s.EditOAuthDescription);
        var oauthColumn = WinoLayout.VStack(6, oauthAction, oauthDescription);
        oauthColumn.Alignment = UIStackViewAlignment.Fill;
        var oauthRow = grid.Row(Translator.ImapSetup_OAuthSignInHeader, oauthColumn);
        Form.Visible(oauthRow, nameof(vm.IsEditOAuthSignInVisible), s => s.IsEditOAuthSignInVisible);

        var davScope = new WinoInfoBar(WinoInfoBarSeverity.Warning, null, Translator.ImapSetup_OAuthDavScopeSignInRequired);
        Form.Visible(davScope, nameof(vm.IsDavScopeSignInRequiredForEditVisible), s => s.IsDavScopeSignInRequiredForEditVisible);

        var group = OnboardingPageView.Group(Translator.ImapSetup_SignInGroup, Column(grid, davScope));
        Form.Visible(group, nameof(vm.IsEditSignInVisible), s => s.IsEditSignInVisible);
        return group;
    }

    /// <summary>The server sections, with discovery results and a connection test.</summary>
    private UIView BuildServers()
    {
        var vm = ViewModel;
        var stack = WinoLayout.VStack(WinoStyle.Space3);
        stack.Alignment = UIStackViewAlignment.Fill;

        var found = new WinoInfoBar(WinoInfoBarSeverity.Success);
        Form.Bind(nameof(vm.DiscoveryFoundMessage), s => s.DiscoveryFoundMessage, text => found.Message = text);
        Form.Visible(found, nameof(vm.IsDiscoveryFoundVisible), s => s.IsDiscoveryFoundVisible);
        stack.AddArrangedSubview(found);
        var notFound = new WinoInfoBar(WinoInfoBarSeverity.Warning);
        Form.Bind(nameof(vm.DiscoveryNotFoundMessage), s => s.DiscoveryNotFoundMessage, text => notFound.Message = text);
        Form.Visible(notFound, nameof(vm.IsDiscoveryNotFoundVisible), s => s.IsDiscoveryNotFoundVisible);
        stack.AddArrangedSubview(notFound);
        var oauthServers = new WinoInfoBar(WinoInfoBarSeverity.Informational);
        Form.Bind(nameof(vm.OAuthServerSignInText), s => s.OAuthServerSignInText, text => oauthServers.Message = text);
        Form.Visible(oauthServers, nameof(vm.IsOAuthServerSignInVisible), s => s.IsOAuthServerSignInVisible);
        stack.AddArrangedSubview(oauthServers);

        var test = Form.Command(Translator.ImapSetup_TestConnections, vm.TestConnectionsCommand);
        var heading = WinoLayout.HStack(WinoStyle.Space2, WinoStyle.Label(Translator.ImapSetup_ConnectionsGroup, WinoStyle.BodyStrong), WinoLayout.Spacer(), test);
        heading.Alignment = UIStackViewAlignment.Center;
        stack.AddArrangedSubview(heading);

        stack.AddArrangedSubview(BuildIncoming());
        stack.AddArrangedSubview(BuildOutgoing());
        stack.AddArrangedSubview(BuildCalDav());
        stack.AddArrangedSubview(BuildCardDav());
        stack.AddArrangedSubview(BuildAdvanced());

        Form.Visible(stack, nameof(vm.IsServersStepVisible), s => s.IsServersStepVisible);
        return stack;
    }

    private UIView BuildIncoming()
    {
        var vm = ViewModel;
        var status = new ConnectionStatusView();
        Form.Bind(nameof(vm.MailConnectionState), s => s.MailConnectionState, state => status.State = state);
        var expander = Expander(vm.IncomingSettingsTitle, WinoIconGlyph.ArrowDownload, status,
            nameof(vm.IsIncomingExpanded), s => s.IsIncomingExpanded, (s, v) => s.IsIncomingExpanded = v);
        Form.Bind(nameof(vm.IncomingSettingsTitle), s => s.IncomingSettingsTitle, text => expander.HeaderCard.Header = text);
        Form.Bind(nameof(vm.IncomingSummary), s => s.IncomingSummary, text => expander.HeaderCard.Description = text);
        Form.Visible(expander, nameof(vm.IsMailSupportEnabled), s => s.IsMailSupportEnabled);

        var error = new WinoInfoBar(WinoInfoBarSeverity.Error, Translator.IMAPSetupDialog_ValidationFailed_Title);
        Form.Bind(nameof(vm.MailConnectionError), s => s.MailConnectionError, text => error.Message = text);
        Form.Visible(error, nameof(vm.HasMailConnectionError), s => s.HasMailConnectionError);

        var grid = new FormGrid();
        grid.Row((string?)null, error);
        grid.Row(Translator.ImapSetup_ServerHeader, HostAndPort(
            Form.Text(nameof(vm.IncomingServer), s => s.IncomingServer, (s, v) => s.IncomingServer = v, null, Translator.ImapSetup_ServerHeader),
            Form.Text(nameof(vm.IncomingServerPort), s => s.IncomingServerPort, (s, v) => s.IncomingServerPort = v, null, Translator.IMAPSetupDialog_IncomingMailServerPort)));
        grid.Row(Translator.ImapCalDavSettingsPage_ConnectionSecurityHeader, Form.PopUp(vm.AvailableConnectionSecurityDisplayNames.ToArray(),
            nameof(vm.SelectedIncomingServerConnectionSecurityIndex), s => s.SelectedIncomingServerConnectionSecurityIndex,
            (s, v) => s.SelectedIncomingServerConnectionSecurityIndex = v, Translator.ImapCalDavSettingsPage_ConnectionSecurityHeader));
        grid.Row(Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader, AuthenticationMethodPopUp(
            nameof(vm.SelectedIncomingServerAuthenticationMethodIndex), s => s.SelectedIncomingServerAuthenticationMethodIndex,
            (s, v) => s.SelectedIncomingServerAuthenticationMethodIndex = v));
        var user = grid.Row(Translator.IMAPSetupDialog_Username, Form.Text(nameof(vm.IncomingServerUsername), s => s.IncomingServerUsername,
            (s, v) => s.IncomingServerUsername = v, null, Translator.IMAPSetupDialog_Username));
        var password = grid.Row(Translator.IMAPSetupDialog_Password, Form.Secure(nameof(vm.IncomingServerPassword), s => s.IncomingServerPassword,
            (s, v) => s.IncomingServerPassword = v, Translator.IMAPSetupDialog_Password));
        Form.Visible(user, nameof(vm.IsIncomingPasswordSignInVisible), s => s.IsIncomingPasswordSignInVisible);
        Form.Visible(password, nameof(vm.IsIncomingPasswordSignInVisible), s => s.IsIncomingPasswordSignInVisible);
        expander.Add(grid);
        return expander;
    }

    private UIView BuildOutgoing()
    {
        var vm = ViewModel;
        var status = new ConnectionStatusView();
        Form.Bind(nameof(vm.MailConnectionState), s => s.MailConnectionState, state => status.State = state);
        var expander = Expander(Translator.ImapSetup_OutgoingTitle, WinoIconGlyph.ArrowUpload, status,
            nameof(vm.IsOutgoingExpanded), s => s.IsOutgoingExpanded, (s, v) => s.IsOutgoingExpanded = v);
        Form.Bind(nameof(vm.OutgoingSummary), s => s.OutgoingSummary, text => expander.HeaderCard.Description = text);
        Form.Visible(expander, nameof(vm.IsMailSupportEnabled), s => s.IsMailSupportEnabled);

        var grid = new FormGrid();
        grid.Row(Translator.ImapSetup_ServerHeader, HostAndPort(
            Form.Text(nameof(vm.OutgoingServer), s => s.OutgoingServer, (s, v) => s.OutgoingServer = v, null, Translator.ImapSetup_ServerHeader),
            Form.Text(nameof(vm.OutgoingServerPort), s => s.OutgoingServerPort, (s, v) => s.OutgoingServerPort = v, null, Translator.IMAPSetupDialog_OutgoingMailServerPort)));
        grid.Row(Translator.ImapCalDavSettingsPage_ConnectionSecurityHeader, Form.PopUp(vm.AvailableConnectionSecurityDisplayNames.ToArray(),
            nameof(vm.SelectedOutgoingServerConnectionSecurityIndex), s => s.SelectedOutgoingServerConnectionSecurityIndex,
            (s, v) => s.SelectedOutgoingServerConnectionSecurityIndex = v, Translator.ImapCalDavSettingsPage_ConnectionSecurityHeader));
        grid.Row(Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader, AuthenticationMethodPopUp(
            nameof(vm.SelectedOutgoingServerAuthenticationMethodIndex), s => s.SelectedOutgoingServerAuthenticationMethodIndex,
            (s, v) => s.SelectedOutgoingServerAuthenticationMethodIndex = v));
        var shared = grid.Row((string?)null, Form.Check(Translator.ImapSetup_UseIncomingSignInForOutgoing, nameof(vm.UseIncomingCredentialsForOutgoing),
            s => s.UseIncomingCredentialsForOutgoing, (s, v) => s.UseIncomingCredentialsForOutgoing = v));
        Form.Visible(shared, nameof(vm.IsOutgoingSharedSignInOptionVisible), s => s.IsOutgoingSharedSignInOptionVisible);
        var user = grid.Row(Translator.IMAPSetupDialog_OutgoingMailServerUsername, Form.Text(nameof(vm.OutgoingServerUsername), s => s.OutgoingServerUsername,
            (s, v) => s.OutgoingServerUsername = v, null, Translator.IMAPSetupDialog_OutgoingMailServerUsername));
        var password = grid.Row(Translator.IMAPSetupDialog_OutgoingMailServerPassword, Form.Secure(nameof(vm.OutgoingServerPassword), s => s.OutgoingServerPassword,
            (s, v) => s.OutgoingServerPassword = v, Translator.IMAPSetupDialog_OutgoingMailServerPassword));
        Form.Visible(user, nameof(vm.IsOutgoingCredentialsVisible), s => s.IsOutgoingCredentialsVisible);
        Form.Visible(password, nameof(vm.IsOutgoingCredentialsVisible), s => s.IsOutgoingCredentialsVisible);
        expander.Add(grid);
        return expander;
    }

    private UIView BuildCalDav()
    {
        var vm = ViewModel;
        var status = new ConnectionStatusView();
        Form.Bind(nameof(vm.CalDavConnectionState), s => s.CalDavConnectionState, state => status.State = state);
        var expander = Expander(Translator.ImapSetup_CalDavTitle, WinoIconGlyph.Calendar, status,
            nameof(vm.IsCalDavExpanded), s => s.IsCalDavExpanded, (s, v) => s.IsCalDavExpanded = v);
        Form.Bind(nameof(vm.CalDavSummary), s => s.CalDavSummary, text => expander.HeaderCard.Description = text);
        Form.Visible(expander, nameof(vm.IsCalDavSettingsVisible), s => s.IsCalDavSettingsVisible);

        var error = new WinoInfoBar(WinoInfoBarSeverity.Error, Translator.IMAPSetupDialog_ValidationFailed_Title);
        Form.Bind(nameof(vm.CalDavConnectionError), s => s.CalDavConnectionError, text => error.Message = text);
        Form.Visible(error, nameof(vm.HasCalDavConnectionError), s => s.HasCalDavConnectionError);

        var grid = new FormGrid();
        grid.Row((string?)null, error);
        var url = Form.Text(nameof(vm.CalDavServiceUrl), s => s.CalDavServiceUrl, (s, v) => s.CalDavServiceUrl = v, null, Translator.ImapCalDavSettingsPage_CalDavServiceUrl);
        url.KeyboardType = UIKeyboardType.Url;
        grid.Row(Translator.ImapCalDavSettingsPage_CalDavServiceUrl, url);
        var secureNote = grid.Row((string?)null, FormGrid.Help(Translator.ImapSetup_DavUsesSecureSignIn));
        Form.Visible(secureNote, nameof(vm.IsCalDavOAuthSelected), s => s.IsCalDavOAuthSelected);
        AddDavCredentials(grid, nameof(vm.IsDavCredentialsOptionVisible), s => s.IsDavCredentialsOptionVisible,
            nameof(vm.IsDavCredentialsVisible), s => s.IsDavCredentialsVisible);
        expander.Add(grid);
        return expander;
    }

    private UIView BuildCardDav()
    {
        var vm = ViewModel;
        var expander = Expander(Translator.ImapSetup_CardDavTitle, WinoIconGlyph.People, null,
            nameof(vm.IsCardDavExpanded), s => s.IsCardDavExpanded, (s, v) => s.IsCardDavExpanded = v);
        Form.Bind(nameof(vm.CardDavSummary), s => s.CardDavSummary, text => expander.HeaderCard.Description = text);
        Form.Visible(expander, nameof(vm.IsCardDavSettingsVisible), s => s.IsCardDavSettingsVisible);

        var grid = new FormGrid();
        var url = Form.Text(nameof(vm.CardDavServiceUrl), s => s.CardDavServiceUrl, (s, v) => s.CardDavServiceUrl = v,
            Translator.ImapCalDavSettingsPage_CardDavDiscoveryPlaceholder, Translator.ImapCalDavSettingsPage_CardDavServiceUrl);
        url.KeyboardType = UIKeyboardType.Url;
        grid.Row(Translator.ImapCalDavSettingsPage_CardDavServiceUrl, url);
        var help = FormGrid.Help();
        Form.Label(help, nameof(vm.CardDavUrlDescription), s => s.CardDavUrlDescription);
        var helpRow = grid.Row((string?)null, help);
        Form.Visible(helpRow, nameof(vm.CardDavUrlDescription), s => !string.IsNullOrEmpty(s.CardDavUrlDescription));
        AddDavCredentials(grid, nameof(vm.IsCardDavCredentialsOptionVisible), s => s.IsCardDavCredentialsOptionVisible,
            nameof(vm.IsCardDavCredentialsVisible), s => s.IsCardDavCredentialsVisible);
        expander.Add(grid);
        return expander;
    }

    /// <summary>The "use the mail sign-in" option and the separate DAV sign-in it hides.</summary>
    private void AddDavCredentials(FormGrid grid, string optionProperty, Func<ImapCalDavSettingsPageViewModel, bool> optionVisible,
        string credentialsProperty, Func<ImapCalDavSettingsPageViewModel, bool> credentialsVisible)
    {
        var vm = ViewModel;
        var option = grid.Row((string?)null, Form.Check(Translator.ImapSetup_UseMailSignInForDav, nameof(vm.UseMailCredentialsForDav),
            s => s.UseMailCredentialsForDav, (s, v) => s.UseMailCredentialsForDav = v));
        Form.Visible(option, optionProperty, optionVisible);
        var user = grid.Row(Translator.ImapCalDavSettingsPage_DavUsername, Form.Text(nameof(vm.CalDavUsername), s => s.CalDavUsername,
            (s, v) => s.CalDavUsername = v, null, Translator.ImapCalDavSettingsPage_DavUsername));
        var password = grid.Row(Translator.ImapCalDavSettingsPage_DavPassword, Form.Secure(nameof(vm.CalDavPassword), s => s.CalDavPassword,
            (s, v) => s.CalDavPassword = v, Translator.ImapCalDavSettingsPage_DavPassword));
        Form.Visible(user, credentialsProperty, credentialsVisible);
        Form.Visible(password, credentialsProperty, credentialsVisible);
    }

    private UIView BuildAdvanced()
    {
        var vm = ViewModel;
        // Bound, because opening Advanced lists OAuth 2.0 among the authentication methods.
        var expander = Expander(Translator.ImapSetup_AdvancedTitle, WinoIconGlyph.Settings, null,
            nameof(vm.IsAdvancedExpanded), s => s.IsAdvancedExpanded, (s, v) => s.IsAdvancedExpanded = v);
        expander.HeaderCard.Description = Translator.ImapSetup_AdvancedDescription;
        var grid = new FormGrid();
        var proxyPort = Form.Text(nameof(vm.ProxyServerPort), s => s.ProxyServerPort, (s, v) => s.ProxyServerPort = v,
            Translator.IMAPSetupDialog_IncomingMailServerPort, Translator.IMAPSetupDialog_IncomingMailServerPort);
        grid.Row(Translator.ImapSetup_ProxyTitle, HostAndPort(
            Form.Text(nameof(vm.ProxyServer), s => s.ProxyServer, (s, v) => s.ProxyServer = v, Translator.ImapSetup_ServerHeader, Translator.ImapSetup_ProxyTitle),
            proxyPort));
        grid.Row((string?)null, FormGrid.Help(Translator.ImapSetup_ProxyDescription));

        var stepper = new UIStepper { MinimumValue = 1, MaximumValue = 20, StepValue = 1, Wraps = false, TranslatesAutoresizingMaskIntoConstraints = false };
        stepper.AccessibilityLabel = Translator.ImapSetup_ConcurrencyTitle;
        var count = WinoStyle.Label(string.Empty, WinoStyle.Body);
        Form.Bind(nameof(vm.MaxConcurrentClients), s => s.MaxConcurrentClients, value =>
        {
            stepper.Value = value;
            count.Text = value.ToString();
            stepper.AccessibilityValue = count.Text;
        });
        EventHandler stepped = (_, _) => vm.MaxConcurrentClientsValue = stepper.Value;
        stepper.ValueChanged += stepped;
        Bindings.Own(new ActionDisposable(() => stepper.ValueChanged -= stepped));
        var concurrencyControl = WinoLayout.HStack(WinoStyle.Space3, count, WinoLayout.Spacer(), stepper);
        concurrencyControl.Alignment = UIStackViewAlignment.Center;
        var concurrency = grid.Row(Translator.ImapSetup_ConcurrencyTitle, concurrencyControl);
        var concurrencyHelp = grid.Row((string?)null, FormGrid.Help(Translator.ImapSetup_ConcurrencyDescription));
        Form.Visible(concurrency, nameof(vm.IsMailAdvancedOptionsVisible), s => s.IsMailAdvancedOptionsVisible);
        Form.Visible(concurrencyHelp, nameof(vm.IsMailAdvancedOptionsVisible), s => s.IsMailAdvancedOptionsVisible);

        var append = grid.Row((string?)null, Form.Check(Translator.SettingsAccountManagementAppendMessage_Title,
            nameof(vm.ShouldAppendMessagesToSentFolder), s => s.ShouldAppendMessagesToSentFolder, (s, v) => s.ShouldAppendMessagesToSentFolder = v));
        var appendHelp = grid.Row((string?)null, FormGrid.Help(Translator.SettingsAccountManagementAppendMessage_Description));
        Form.Visible(append, nameof(vm.IsMailAdvancedOptionsVisible), s => s.IsMailAdvancedOptionsVisible);
        Form.Visible(appendHelp, nameof(vm.IsMailAdvancedOptionsVisible), s => s.IsMailAdvancedOptionsVisible);
        expander.Add(grid);
        return expander;
    }

    /// <summary>
    /// The authentication method list changes while the page is open (OAuth 2.0 joins it once discovery, a sign-in or
    /// Advanced offers it), so the menu rebuilds from the collection and reselects the current method.
    /// </summary>
    private UIButton AuthenticationMethodPopUp(string property, Func<ImapCalDavSettingsPageViewModel, int> read, Action<ImapCalDavSettingsPageViewModel, int> write)
    {
        var vm = ViewModel;
        var methods = vm.AvailableAuthenticationMethodDisplayNames;
        var popUp = new WinoPopUpButton(Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader);
        void Render() => popUp.SetItems(methods.ToArray(), read(vm));
        var binding = Bindings.Own(new PropertyBinding<ImapCalDavSettingsPageViewModel, int>(vm, property, read, _ => Render(), Dispatcher, ReportError, write));
        popUp.Chosen = index => binding.UpdateSource(index);
        NotifyCollectionChangedEventHandler changed = (_, _) => popUp.BeginInvokeOnMainThread(Render);
        methods.CollectionChanged += changed;
        Bindings.Own(new ActionDisposable(() => methods.CollectionChanged -= changed));
        Render();
        return popUp;
    }

    private WinoSettingsExpander Expander(string header, WinoIconGlyph icon, UIView? status,
        string expandedProperty, Func<ImapCalDavSettingsPageViewModel, bool> read, Action<ImapCalDavSettingsPageViewModel, bool> write)
    {
        var expander = new WinoSettingsExpander(header, null, icon, status);
        var binding = Bindings.Own(new PropertyBinding<ImapCalDavSettingsPageViewModel, bool>(ViewModel, expandedProperty, read,
            value => { if (expander.IsExpanded != value) expander.IsExpanded = value; }, Dispatcher, ReportError, write));
        EventHandler<bool> changed = (_, value) => binding.UpdateSource(value);
        expander.IsExpandedChanged += changed;
        Bindings.Own(new ActionDisposable(() => expander.IsExpandedChanged -= changed));
        return expander;
    }

    /// <summary>
    /// Visibility from a computed property. The step change is listened to as well, because the sign-in stages derive
    /// from it without being raised when only the step moves.
    /// </summary>
    private void VisibleWhen(UIView view, Func<ImapCalDavSettingsPageViewModel, bool> read, params string[] properties)
    {
        foreach (var property in properties.Append(nameof(ViewModel.CurrentSetupStep)).Distinct())
            Form.Visible(view, property, read);
    }

    private static UIStackView Column(params UIView[] views)
    {
        var stack = WinoLayout.VStack(WinoStyle.Space3, views);
        stack.Alignment = UIStackViewAlignment.Fill;
        return stack;
    }

    private static void SetTitle(UIButton button, string? text)
    {
        if (button.Configuration is not { } configuration) return;
        configuration.Title = text ?? string.Empty;
        button.Configuration = configuration;
    }

    /// <summary>Host field and a narrow port field on one row.</summary>
    private static UIView HostAndPort(UITextField host, UITextField port)
    {
        host.KeyboardType = UIKeyboardType.Url;
        port.KeyboardType = UIKeyboardType.NumberPad;
        port.WidthAnchor.ConstraintEqualTo(80).Active = true;
        if (string.IsNullOrEmpty(port.Placeholder)) port.Placeholder = Translator.IMAPSetupDialog_IncomingMailServerPort;
        return WinoLayout.HStack(WinoStyle.Space2, host, port);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
#if DEBUG
        RegisterDiagnostics();
#endif
        // The page renders the secure sign-in choice after the address step and in edit mode.
        ViewModel.IsSecureSignInSupportedByHost = true;
        return ViewModel.InitializeAsync(mode, parameter);
    }

#if DEBUG
    /// <summary>imap-step signin|servers and imap-stage identity|method move the page without a server.</summary>
    private void RegisterDiagnostics()
    {
        UIKitDebugBridge.Register("imap-step", async args =>
        {
            var step = args.Length > 0 && args[0].Equals("servers", StringComparison.OrdinalIgnoreCase) ? ImapSetupStep.Servers : ImapSetupStep.SignIn;
            await Dispatcher.ExecuteOnUIThread(() => ViewModel.CurrentSetupStep = step);
            return "ok " + step;
        });
        UIKitDebugBridge.Register("imap-stage", async args =>
        {
            var stage = args.Length > 0 && args[0].Equals("method", StringComparison.OrdinalIgnoreCase) ? ImapSignInStage.Method : ImapSignInStage.Identity;
            await Dispatcher.ExecuteOnUIThread(() => ViewModel.SignInStage = stage);
            return "ok " + stage;
        });
        // imap-oauth offered|confirm|org|signedin|stored|stored-loopback fakes what discovery or a stored sign-in left,
        // so each secure sign-in state renders without a server. Private state, so reflection (Debug only).
        UIKitDebugBridge.Register("imap-oauth", async args =>
        {
            var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "offered";
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(ImapCalDavSettingsPageViewModel);
            var vm = ViewModel;
            var profile = new Wino.Core.Domain.Entities.Shared.AccountOAuthProfile
            {
                Issuer = "https://auth.example.com",
                ClientId = "preview",
                RedirectKind = mode == "stored-loopback" ? OAuthRedirectKind.Loopback : OAuthRedirectKind.CustomScheme,
                TrustSource = OAuthTrustSource.Discovered
            };
            void Set(string field, object? value) => type.GetField(field, flags)!.SetValue(vm, value);
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                var offered = new Wino.Core.Domain.Models.Authorization.OAuthDiscoveryResult { Profile = profile, IssuerHost = "auth.example.com" };
                switch (mode)
                {
                    case "org":
                        Set("_oauthDiscovery", new Wino.Core.Domain.Models.Authorization.OAuthDiscoveryResult { OfferedWithoutClient = true, IssuerHost = "auth.example.com" });
                        break;
                    case "confirm":
                        Set("_oauthDiscovery", new Wino.Core.Domain.Models.Authorization.OAuthDiscoveryResult { Profile = profile, IssuerHost = "auth.example.com", RequiresIssuerConfirmation = true });
                        break;
                    case "signedin":
                        Set("_oauthDiscovery", offered);
                        Set("_oauthSession", new Wino.Core.Domain.Models.Authorization.OAuthSetupSession(Guid.NewGuid(), profile) { HasRefreshToken = true });
                        break;
                    case "stored":
                    case "stored-loopback":
                        Set("_storedOAuthProfile", profile);
                        type.GetMethod("EnsureOAuthMethodListed", flags)!.Invoke(vm, null);
                        vm.SelectedIncomingServerAuthenticationMethodIndex = (int)type.GetMethod("FindAuthenticationMethodIndex", flags)!.Invoke(vm, [ImapAuthenticationMethod.OAuth2])!;
                        break;
                    default:
                        Set("_oauthDiscovery", offered);
                        break;
                }
                type.GetMethod("NotifySecureSignInLayoutChanged", flags)!.Invoke(vm, null);
            });
            return "ok " + mode;
        });
    }
#endif

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { ReportError(exception); }
    }
}
