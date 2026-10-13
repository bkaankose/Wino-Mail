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
/// it is a guided flow (sign in, then the discovered servers); editing an account it shows the features, the sign-in
/// and every server section. Server sections are collapsible settings expanders.
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
        Form.Bind(nameof(vm.PrimaryActionText), s => s.PrimaryActionText, text =>
        {
            if (primary.Configuration is not { } configuration) return;
            configuration.Title = text ?? string.Empty;
            primary.Configuration = configuration;
        });

        View = new OnboardingPageView(header, content, busy, [back, cancel], primary, 640);
    }

    /// <summary>Adding an account, first step: who is signing in. Discovery runs on Continue.</summary>
    private UIView BuildSignInStep()
    {
        var vm = ViewModel;
        var grid = new FormGrid();
        AddSignInRows(grid, includeServerAddress: true);
        var group = OnboardingPageView.Group(null, grid);
        Form.Visible(group, nameof(vm.IsSignInStepVisible), s => s.IsSignInStepVisible);
        return group;
    }

    private void AddSignInRows(FormGrid grid, bool includeServerAddress)
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

        if (includeServerAddress)
        {
            // Without mail there is nothing to discover from reliably, so the CalDAV address can be typed here.
            var server = Form.Text(nameof(vm.CalDavServiceUrl), s => s.CalDavServiceUrl, (s, v) => s.CalDavServiceUrl = v,
                Translator.ImapSetup_ServerAddressPlaceholder, Translator.ImapSetup_ServerAddressOptional);
            server.KeyboardType = UIKeyboardType.Url;
            var serverRow = grid.Row(Translator.ImapSetup_ServerAddressOptional, server);
            Form.Visible(serverRow, nameof(vm.IsSignInServerAddressVisible), s => s.IsSignInServerAddressVisible);
        }

        var password = Form.Secure(nameof(vm.Password), s => s.Password, (s, v) => s.Password = v, Translator.IMAPSetupDialog_Password);
        grid.Row(Translator.IMAPSetupDialog_Password, password);
        var help = FormGrid.Help();
        Form.Label(help, nameof(vm.AppPasswordHelpText), s => s.AppPasswordHelpText);
        Form.Visible(help, nameof(vm.AppPasswordHelpText), s => !string.IsNullOrWhiteSpace(s.AppPasswordHelpText));
        var link = Form.Link(Translator.ImapSetup_AppPasswordHelpLink, vm.OpenAppPasswordHelpCommand);
        Form.Visible(link, nameof(vm.HasAppPasswordHelpLink), s => s.HasAppPasswordHelpLink);
        var helpStack = WinoLayout.VStack(4, help, link);
        helpStack.Alignment = UIStackViewAlignment.Leading;
        grid.Row((string?)null, helpStack);
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

    /// <summary>Editing an account: the sign-in shared by every server.</summary>
    private UIView BuildEditSignIn()
    {
        var vm = ViewModel;
        var grid = new FormGrid();
        AddSignInRows(grid, includeServerAddress: false);
        var group = OnboardingPageView.Group(Translator.ImapSetup_SignInGroup, grid);
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
        grid.Row(Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader, Form.PopUp(vm.AvailableAuthenticationMethodDisplayNames.ToArray(),
            nameof(vm.SelectedIncomingServerAuthenticationMethodIndex), s => s.SelectedIncomingServerAuthenticationMethodIndex,
            (s, v) => s.SelectedIncomingServerAuthenticationMethodIndex = v, Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader));
        grid.Row(Translator.IMAPSetupDialog_Username, Form.Text(nameof(vm.IncomingServerUsername), s => s.IncomingServerUsername,
            (s, v) => s.IncomingServerUsername = v, null, Translator.IMAPSetupDialog_Username));
        grid.Row(Translator.IMAPSetupDialog_Password, Form.Secure(nameof(vm.IncomingServerPassword), s => s.IncomingServerPassword,
            (s, v) => s.IncomingServerPassword = v, Translator.IMAPSetupDialog_Password));
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
        grid.Row(Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader, Form.PopUp(vm.AvailableAuthenticationMethodDisplayNames.ToArray(),
            nameof(vm.SelectedOutgoingServerAuthenticationMethodIndex), s => s.SelectedOutgoingServerAuthenticationMethodIndex,
            (s, v) => s.SelectedOutgoingServerAuthenticationMethodIndex = v, Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader));
        grid.Row((string?)null, Form.Check(Translator.ImapSetup_UseIncomingSignInForOutgoing, nameof(vm.UseIncomingCredentialsForOutgoing),
            s => s.UseIncomingCredentialsForOutgoing, (s, v) => s.UseIncomingCredentialsForOutgoing = v));
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
        grid.Row((string?)null, FormGrid.Help(Translator.ImapSetup_CardDavSharedSignIn));
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
        var expander = new WinoSettingsExpander(Translator.ImapSetup_AdvancedTitle, Translator.ImapSetup_AdvancedDescription, WinoIconGlyph.Settings);
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

    /// <summary>Host field and a narrow port field on one row.</summary>
    private static UIView HostAndPort(UITextField host, UITextField port)
    {
        host.KeyboardType = UIKeyboardType.Url;
        port.KeyboardType = UIKeyboardType.NumberPad;
        port.WidthAnchor.ConstraintEqualTo(80).Active = true;
        if (string.IsNullOrEmpty(port.Placeholder)) port.Placeholder = Translator.IMAPSetupDialog_IncomingMailServerPort;
        return WinoLayout.HStack(WinoStyle.Space2, host, port);
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter) => ViewModel.InitializeAsync(mode, parameter);

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { ReportError(exception); }
    }
}
