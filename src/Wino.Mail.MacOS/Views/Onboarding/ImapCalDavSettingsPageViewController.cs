using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Onboarding;

/// <summary>
/// Custom IMAP / SMTP (and CalDAV / CardDAV) server page, the Windows ImapCalDavSettingsPage.
/// Adding an account it is a guided flow (sign in, then the discovered servers); editing an
/// account from Account details it shows the features, the sign-in and every server section.
/// Fields are a Preferences-style grid inside collapsible sections; Return runs the primary action.
/// </summary>
public sealed class ImapCalDavSettingsPageViewController(ImapCalDavSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : WinoViewController<ImapCalDavSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    private FormBinder<ImapCalDavSettingsPageViewModel> _form = null!;
    private NSButton _primary = null!;

    public override void LoadView()
    {
        var vm = ViewModel;
        _form = new FormBinder<ImapCalDavSettingsPageViewModel>(vm, Bindings, Dispatcher, ReportError);

        var content = WinoLayout.VStack(WinoStyle.Space4);
        content.Alignment = NSLayoutAttribute.Leading;
        void Add(NSView view)
        {
            content.AddArrangedSubview(view);
            view.WidthAnchor.ConstraintEqualTo(content.WidthAnchor).Active = true;
        }

        var steps = new SetupStepIndicator();
        _form.Visible(steps, nameof(vm.IsCreateMode), s => s.IsCreateMode);
        _form.Bind(nameof(vm.CurrentSetupStep), s => s.CurrentSetupStep, steps.SetCurrent);

        var pageError = new WinoInfoBar(WinoInfoBarSeverity.Error) { IsClosable = true };
        _form.Bind(nameof(vm.PageInfoBarTitle), s => s.PageInfoBarTitle, text => pageError.Title = text);
        _form.Bind(nameof(vm.PageInfoBarMessage), s => s.PageInfoBarMessage, text => pageError.Message = text);
        _form.Visible(pageError, nameof(vm.IsPageInfoBarOpen), s => s.IsPageInfoBarOpen);
        EventHandler<WinoInfoBarClosedEventArgs> closeError = (_, _) => _ = RunAsync(() => vm.HidePageErrorCommand.ExecuteAsync(null));
        pageError.Closed += closeError;
        Bindings.Own(new ActionDisposable(() => pageError.Closed -= closeError));
        Add(pageError);

        var pop3 = new WinoInfoBar(WinoInfoBarSeverity.Informational, Translator.POP3Setup_LimitationsTitle, Translator.POP3Setup_LimitationsMessage);
        _form.Visible(pop3, nameof(vm.IsPop3), s => s.IsPop3);
        Add(pop3);

        Add(BuildSignInStep());
        Add(BuildEditFeatures());
        Add(BuildEditSignIn());
        Add(BuildServers());

        var title = NSTextField.CreateLabel(string.Empty);
        var description = NSTextField.CreateLabel(string.Empty);
        void UpdateHeader()
        {
            title.StringValue = vm.IsSignInStepVisible ? vm.SignInTitle
                : vm.IsEditMode ? Translator.ImapCalDavSettingsPage_TitleEdit : Translator.ImapCalDavSettingsPage_TitleCreate;
            description.StringValue = vm.IsSignInStepVisible ? vm.SignInDescription : string.Empty;
            description.Hidden = description.StringValue.Length == 0;
        }
        foreach (var property in new[] { nameof(vm.IsSignInStepVisible), nameof(vm.SignInTitle), nameof(vm.SignInDescription), nameof(vm.IsEditMode) })
            _form.Bind(property, _ => 0, _ => UpdateHeader());
        var header = WinoLayout.VStack(WinoStyle.Space4, steps, OnboardingPageView.Header(null, title, description));
        header.Alignment = NSLayoutAttribute.Leading;

        // Footer: progress on the leading side; Back (adding) or Cancel (editing) and the primary action.
        var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, ControlSize = NSControlSize.Small, Indeterminate = true, TranslatesAutoresizingMaskIntoConstraints = false };
        var busyText = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.SecondaryText);
        busyText.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        var busy = WinoLayout.HStack(WinoStyle.Space2, spinner, busyText);
        _form.Label(busyText, nameof(vm.BusyText), s => s.BusyText);
        _form.Bind(nameof(vm.IsBusy), s => s.IsBusy, isBusy =>
        {
            busy.Hidden = !isBusy;
            if (isBusy) spinner.StartAnimation(null); else spinner.StopAnimation(null);
        });

        var back = _form.Command(Translator.Buttons_Back, vm.BackCommand);
        _form.Visible(back, nameof(vm.IsCreateMode), s => s.IsCreateMode);
        var cancel = _form.Command(Translator.Buttons_Cancel, vm.CancelCommand);
        cancel.KeyEquivalent = "\u001b";
        _form.Visible(cancel, nameof(vm.IsEditMode), s => s.IsEditMode);
        _primary = _form.Command(Translator.ProviderSelection_ContinueButton, vm.PrimaryActionCommand);
        _primary.WidthAnchor.ConstraintGreaterThanOrEqualTo(110).Active = true;
        _form.Bind(nameof(vm.PrimaryActionText), s => s.PrimaryActionText, text => _primary.Title = text);

        View = new OnboardingPageView(header, content, busy, [back, cancel], _primary, 640);
    }

    #region Sections

    /// <summary>Adding an account, first step: who is signing in. Discovery runs on Continue.</summary>
    private NSView BuildSignInStep()
    {
        var vm = ViewModel;
        var grid = new FormGrid();
        AddSignInRows(grid, includeServerAddress: true);
        var group = OnboardingPageView.Group(null, grid);
        _form.Visible(group, nameof(vm.IsSignInStepVisible), s => s.IsSignInStepVisible);
        return group;
    }

    private void AddSignInRows(FormGrid grid, bool includeServerAddress)
    {
        var vm = ViewModel;
        var name = _form.Text(nameof(vm.DisplayName), s => s.DisplayName, (s, v) => s.DisplayName = v,
            Translator.IMAPSetupDialog_DisplayNamePlaceholder, Translator.ImapSetup_YourName);
        var nameRow = grid.Row(Translator.ImapSetup_YourName, name);
        var nameHelpRow = grid.Row((NSTextField?)null, FormGrid.Help(Translator.ImapSetup_YourNameDescription));
        _form.Visible(nameRow, nameof(vm.IsSignInSenderNameVisible), s => s.IsSignInSenderNameVisible);
        _form.Visible(nameHelpRow, nameof(vm.IsSignInSenderNameVisible), s => s.IsSignInSenderNameVisible);

        var addressLabel = FormGrid.FieldLabel(Translator.IMAPSetupDialog_MailAddress);
        _form.Label(addressLabel, nameof(vm.SignInAddressHeader), s => s.SignInAddressHeader);
        var address = _form.Text(nameof(vm.EmailAddress), s => s.EmailAddress, (s, v) => s.EmailAddress = v, Translator.IMAPSetupDialog_MailAddressPlaceholder);
        address.ContentType = NSTextContentType.EmailAddress;
        _form.Bind(nameof(vm.SignInAddressHeader), s => s.SignInAddressHeader, text => address.AccessibilityLabel = text);
        grid.Row(addressLabel, address);

        if (includeServerAddress)
        {
            // Without mail there is nothing to discover from reliably, so the CalDAV address can be typed here.
            var server = _form.Text(nameof(vm.CalDavServiceUrl), s => s.CalDavServiceUrl, (s, v) => s.CalDavServiceUrl = v,
                Translator.ImapSetup_ServerAddressPlaceholder, Translator.ImapSetup_ServerAddressOptional);
            var serverRow = grid.Row(Translator.ImapSetup_ServerAddressOptional, server);
            _form.Visible(serverRow, nameof(vm.IsSignInServerAddressVisible), s => s.IsSignInServerAddressVisible);
        }

        var password = _form.Secure(nameof(vm.Password), s => s.Password, (s, v) => s.Password = v, Translator.IMAPSetupDialog_Password);
        grid.Row(Translator.IMAPSetupDialog_Password, password);
        var help = FormGrid.Help();
        _form.Label(help, nameof(vm.AppPasswordHelpText), s => s.AppPasswordHelpText);
        _form.Visible(help, nameof(vm.AppPasswordHelpText), s => !string.IsNullOrWhiteSpace(s.AppPasswordHelpText));
        var link = _form.Link(Translator.ImapSetup_AppPasswordHelpLink, vm.OpenAppPasswordHelpCommand);
        _form.Visible(link, nameof(vm.HasAppPasswordHelpLink), s => s.HasAppPasswordHelpLink);
        var helpStack = WinoLayout.VStack(4, help, link);
        helpStack.Alignment = NSLayoutAttribute.Leading;
        grid.Row((NSTextField?)null, helpStack);
    }

    /// <summary>
    /// Editing an account: what it is used for, as the provider step's capability cards (Windows
    /// AccountCapabilityPicker on ImapCalDavSettingsPage). Turning Calendar or Contacts off hides the
    /// CalDAV / CardDAV sections; To Do stays on this device because IMAP has no task service.
    /// </summary>
    private NSView BuildEditFeatures()
    {
        var vm = ViewModel;
        var picker = new AccountCapabilityPicker(vm.Capabilities, () => Translator.ProviderSelection_ModeImap, null, Bindings, Dispatcher, ReportError);
        var missing = WinoStyle.Label(Translator.ProviderSelection_CapabilityValidationMessage, WinoStyle.Caption, WinoStyle.Critical, 0);
        var capabilities = new FormBinder<AccountCapabilitySelection>(vm.Capabilities, Bindings, Dispatcher, ReportError);
        capabilities.Visible(missing, nameof(AccountCapabilitySelection.IsSelectionMissing), s => s.IsSelectionMissing);
        var column = WinoLayout.VStack(WinoStyle.Space3, picker, missing);
        foreach (var view in column.ArrangedSubviews) view.WidthAnchor.ConstraintEqualTo(column.WidthAnchor).Active = true;
        var group = OnboardingPageView.Group(Translator.ImapSetup_FeaturesGroup, column);
        _form.Visible(group, nameof(vm.IsEditMode), s => s.IsEditMode);
        return group;
    }

    /// <summary>Editing an account: the sign-in shared by every server.</summary>
    private NSView BuildEditSignIn()
    {
        var vm = ViewModel;
        var grid = new FormGrid();
        AddSignInRows(grid, includeServerAddress: false);
        var group = OnboardingPageView.Group(Translator.ImapSetup_SignInGroup, grid);
        _form.Visible(group, nameof(vm.IsEditSignInVisible), s => s.IsEditSignInVisible);
        return group;
    }

    /// <summary>The server sections, with discovery results and a connection test.</summary>
    private NSView BuildServers()
    {
        var vm = ViewModel;
        var stack = WinoLayout.VStack(WinoStyle.Space3);
        stack.Alignment = NSLayoutAttribute.Leading;
        void Add(NSView view)
        {
            stack.AddArrangedSubview(view);
            view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        }

        var found = new WinoInfoBar(WinoInfoBarSeverity.Success);
        _form.Bind(nameof(vm.DiscoveryFoundMessage), s => s.DiscoveryFoundMessage, text => found.Message = text);
        _form.Visible(found, nameof(vm.IsDiscoveryFoundVisible), s => s.IsDiscoveryFoundVisible);
        Add(found);
        var notFound = new WinoInfoBar(WinoInfoBarSeverity.Warning);
        _form.Bind(nameof(vm.DiscoveryNotFoundMessage), s => s.DiscoveryNotFoundMessage, text => notFound.Message = text);
        _form.Visible(notFound, nameof(vm.IsDiscoveryNotFoundVisible), s => s.IsDiscoveryNotFoundVisible);
        Add(notFound);

        var test = _form.Command(Translator.ImapSetup_TestConnections, vm.TestConnectionsCommand);
        var heading = WinoLayout.HStack(WinoStyle.Space2, WinoStyle.Label(Translator.ImapSetup_ConnectionsGroup, WinoStyle.BodyStrong), WinoLayout.Spacer(), test);
        Add(heading);

        Add(BuildIncoming());
        Add(BuildOutgoing());
        Add(BuildCalDav());
        Add(BuildCardDav());
        Add(BuildAdvanced());

        _form.Visible(stack, nameof(vm.IsServersStepVisible), s => s.IsServersStepVisible);
        return stack;
    }

    private NSView BuildIncoming()
    {
        var vm = ViewModel;
        var status = new ConnectionStatusView();
        _form.Bind(nameof(vm.MailConnectionState), s => s.MailConnectionState, state => status.State = state);
        var expander = Expander(vm.IncomingSettingsTitle, WinoIconGlyph.ArrowDownload, status,
            nameof(vm.IsIncomingExpanded), s => s.IsIncomingExpanded, (s, v) => s.IsIncomingExpanded = v);
        _form.Bind(nameof(vm.IncomingSettingsTitle), s => s.IncomingSettingsTitle, text => expander.HeaderCard.Header = text);
        _form.Bind(nameof(vm.IncomingSummary), s => s.IncomingSummary, text => expander.HeaderCard.Description = text);
        _form.Visible(expander, nameof(vm.IsMailSupportEnabled), s => s.IsMailSupportEnabled);

        var error = new WinoInfoBar(WinoInfoBarSeverity.Error, Translator.IMAPSetupDialog_ValidationFailed_Title);
        _form.Bind(nameof(vm.MailConnectionError), s => s.MailConnectionError, text => error.Message = text);
        _form.Visible(error, nameof(vm.HasMailConnectionError), s => s.HasMailConnectionError);

        var grid = new FormGrid();
        grid.Row(Translator.ImapSetup_ServerHeader, HostAndPort(
            _form.Text(nameof(vm.IncomingServer), s => s.IncomingServer, (s, v) => s.IncomingServer = v, null, Translator.ImapSetup_ServerHeader),
            _form.Text(nameof(vm.IncomingServerPort), s => s.IncomingServerPort, (s, v) => s.IncomingServerPort = v, null, Translator.IMAPSetupDialog_IncomingMailServerPort)));
        grid.Row(Translator.ImapCalDavSettingsPage_ConnectionSecurityHeader, _form.PopUp(vm.AvailableConnectionSecurityDisplayNames,
            nameof(vm.SelectedIncomingServerConnectionSecurityIndex), s => s.SelectedIncomingServerConnectionSecurityIndex,
            (s, v) => s.SelectedIncomingServerConnectionSecurityIndex = v, Translator.ImapCalDavSettingsPage_ConnectionSecurityHeader));
        grid.Row(Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader, _form.PopUp(vm.AvailableAuthenticationMethodDisplayNames,
            nameof(vm.SelectedIncomingServerAuthenticationMethodIndex), s => s.SelectedIncomingServerAuthenticationMethodIndex,
            (s, v) => s.SelectedIncomingServerAuthenticationMethodIndex = v, Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader));
        grid.Row(Translator.IMAPSetupDialog_Username, _form.Text(nameof(vm.IncomingServerUsername), s => s.IncomingServerUsername,
            (s, v) => s.IncomingServerUsername = v, null, Translator.IMAPSetupDialog_Username));
        grid.Row(Translator.IMAPSetupDialog_Password, _form.Secure(nameof(vm.IncomingServerPassword), s => s.IncomingServerPassword,
            (s, v) => s.IncomingServerPassword = v, Translator.IMAPSetupDialog_Password));

        expander.Add(Column(error, grid));
        return expander;
    }

    private NSView BuildOutgoing()
    {
        var vm = ViewModel;
        var status = new ConnectionStatusView();
        _form.Bind(nameof(vm.MailConnectionState), s => s.MailConnectionState, state => status.State = state);
        var expander = Expander(Translator.ImapSetup_OutgoingTitle, WinoIconGlyph.ArrowUpload, status,
            nameof(vm.IsOutgoingExpanded), s => s.IsOutgoingExpanded, (s, v) => s.IsOutgoingExpanded = v);
        _form.Bind(nameof(vm.OutgoingSummary), s => s.OutgoingSummary, text => expander.HeaderCard.Description = text);
        _form.Visible(expander, nameof(vm.IsMailSupportEnabled), s => s.IsMailSupportEnabled);

        var grid = new FormGrid();
        grid.Row(Translator.ImapSetup_ServerHeader, HostAndPort(
            _form.Text(nameof(vm.OutgoingServer), s => s.OutgoingServer, (s, v) => s.OutgoingServer = v, null, Translator.ImapSetup_ServerHeader),
            _form.Text(nameof(vm.OutgoingServerPort), s => s.OutgoingServerPort, (s, v) => s.OutgoingServerPort = v, null, Translator.IMAPSetupDialog_OutgoingMailServerPort)));
        grid.Row(Translator.ImapCalDavSettingsPage_ConnectionSecurityHeader, _form.PopUp(vm.AvailableConnectionSecurityDisplayNames,
            nameof(vm.SelectedOutgoingServerConnectionSecurityIndex), s => s.SelectedOutgoingServerConnectionSecurityIndex,
            (s, v) => s.SelectedOutgoingServerConnectionSecurityIndex = v, Translator.ImapCalDavSettingsPage_ConnectionSecurityHeader));
        grid.Row(Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader, _form.PopUp(vm.AvailableAuthenticationMethodDisplayNames,
            nameof(vm.SelectedOutgoingServerAuthenticationMethodIndex), s => s.SelectedOutgoingServerAuthenticationMethodIndex,
            (s, v) => s.SelectedOutgoingServerAuthenticationMethodIndex = v, Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader));
        grid.Row((NSTextField?)null, _form.Check(Translator.ImapSetup_UseIncomingSignInForOutgoing, nameof(vm.UseIncomingCredentialsForOutgoing),
            s => s.UseIncomingCredentialsForOutgoing, (s, v) => s.UseIncomingCredentialsForOutgoing = v));
        var user = grid.Row(Translator.IMAPSetupDialog_OutgoingMailServerUsername, _form.Text(nameof(vm.OutgoingServerUsername), s => s.OutgoingServerUsername,
            (s, v) => s.OutgoingServerUsername = v, null, Translator.IMAPSetupDialog_OutgoingMailServerUsername));
        var password = grid.Row(Translator.IMAPSetupDialog_OutgoingMailServerPassword, _form.Secure(nameof(vm.OutgoingServerPassword), s => s.OutgoingServerPassword,
            (s, v) => s.OutgoingServerPassword = v, Translator.IMAPSetupDialog_OutgoingMailServerPassword));
        _form.Visible(user, nameof(vm.IsOutgoingCredentialsVisible), s => s.IsOutgoingCredentialsVisible);
        _form.Visible(password, nameof(vm.IsOutgoingCredentialsVisible), s => s.IsOutgoingCredentialsVisible);

        expander.Add(grid);
        return expander;
    }

    private NSView BuildCalDav()
    {
        var vm = ViewModel;
        var status = new ConnectionStatusView();
        _form.Bind(nameof(vm.CalDavConnectionState), s => s.CalDavConnectionState, state => status.State = state);
        var expander = Expander(Translator.ImapSetup_CalDavTitle, WinoIconGlyph.Calendar, status,
            nameof(vm.IsCalDavExpanded), s => s.IsCalDavExpanded, (s, v) => s.IsCalDavExpanded = v);
        _form.Bind(nameof(vm.CalDavSummary), s => s.CalDavSummary, text => expander.HeaderCard.Description = text);
        _form.Visible(expander, nameof(vm.IsCalDavSettingsVisible), s => s.IsCalDavSettingsVisible);

        var error = new WinoInfoBar(WinoInfoBarSeverity.Error, Translator.IMAPSetupDialog_ValidationFailed_Title);
        _form.Bind(nameof(vm.CalDavConnectionError), s => s.CalDavConnectionError, text => error.Message = text);
        _form.Visible(error, nameof(vm.HasCalDavConnectionError), s => s.HasCalDavConnectionError);

        var grid = new FormGrid();
        grid.Row(Translator.ImapCalDavSettingsPage_CalDavServiceUrl, _form.Text(nameof(vm.CalDavServiceUrl), s => s.CalDavServiceUrl,
            (s, v) => s.CalDavServiceUrl = v, null, Translator.ImapCalDavSettingsPage_CalDavServiceUrl));
        AddDavCredentials(grid, nameof(vm.IsDavCredentialsOptionVisible), s => s.IsDavCredentialsOptionVisible,
            nameof(vm.IsDavCredentialsVisible), s => s.IsDavCredentialsVisible);

        expander.Add(Column(error, grid));
        return expander;
    }

    private NSView BuildCardDav()
    {
        var vm = ViewModel;
        var expander = Expander(Translator.ImapSetup_CardDavTitle, WinoIconGlyph.People, null,
            nameof(vm.IsCardDavExpanded), s => s.IsCardDavExpanded, (s, v) => s.IsCardDavExpanded = v);
        _form.Bind(nameof(vm.CardDavSummary), s => s.CardDavSummary, text => expander.HeaderCard.Description = text);
        _form.Visible(expander, nameof(vm.IsCardDavSettingsVisible), s => s.IsCardDavSettingsVisible);

        var grid = new FormGrid();
        grid.Row(Translator.ImapCalDavSettingsPage_CardDavServiceUrl, _form.Text(nameof(vm.CardDavServiceUrl), s => s.CardDavServiceUrl,
            (s, v) => s.CardDavServiceUrl = v, Translator.ImapCalDavSettingsPage_CardDavDiscoveryPlaceholder, Translator.ImapCalDavSettingsPage_CardDavServiceUrl));
        grid.Row((NSTextField?)null, FormGrid.Help(Translator.ImapSetup_CardDavSharedSignIn));
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
        var option = grid.Row((NSTextField?)null, _form.Check(Translator.ImapSetup_UseMailSignInForDav, nameof(vm.UseMailCredentialsForDav),
            s => s.UseMailCredentialsForDav, (s, v) => s.UseMailCredentialsForDav = v));
        _form.Visible(option, optionProperty, optionVisible);
        var user = grid.Row(Translator.ImapCalDavSettingsPage_DavUsername, _form.Text(nameof(vm.CalDavUsername), s => s.CalDavUsername,
            (s, v) => s.CalDavUsername = v, null, Translator.ImapCalDavSettingsPage_DavUsername));
        var password = grid.Row(Translator.ImapCalDavSettingsPage_DavPassword, _form.Secure(nameof(vm.CalDavPassword), s => s.CalDavPassword,
            (s, v) => s.CalDavPassword = v, Translator.ImapCalDavSettingsPage_DavPassword));
        _form.Visible(user, credentialsProperty, credentialsVisible);
        _form.Visible(password, credentialsProperty, credentialsVisible);
    }

    private NSView BuildAdvanced()
    {
        var vm = ViewModel;
        var expander = new WinoSettingsExpander(Translator.ImapSetup_AdvancedTitle, Translator.ImapSetup_AdvancedDescription, WinoIconGlyph.Settings);

        var grid = new FormGrid();
        var proxyPort = _form.Text(nameof(vm.ProxyServerPort), s => s.ProxyServerPort, (s, v) => s.ProxyServerPort = v,
            Translator.IMAPSetupDialog_IncomingMailServerPort, Translator.IMAPSetupDialog_IncomingMailServerPort);
        grid.Row(Translator.ImapSetup_ProxyTitle, HostAndPort(
            _form.Text(nameof(vm.ProxyServer), s => s.ProxyServer, (s, v) => s.ProxyServer = v, Translator.ImapSetup_ServerHeader, Translator.ImapSetup_ProxyTitle),
            proxyPort));
        grid.Row((NSTextField?)null, FormGrid.Help(Translator.ImapSetup_ProxyDescription));

        var stepper = new NSStepper { MinValue = 1, MaxValue = 20, Increment = 1, ValueWraps = false, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(stepper, Translator.ImapSetup_ConcurrencyTitle);
        var count = WinoStyle.Label(string.Empty, WinoStyle.Body);
        _form.Bind(nameof(vm.MaxConcurrentClients), s => s.MaxConcurrentClients, value =>
        {
            stepper.IntValue = value;
            count.StringValue = value.ToString();
            ((NSView)stepper).AccessibilityValue = new Foundation.NSString(count.StringValue);
        });
        EventHandler stepped = (_, _) => vm.MaxConcurrentClientsValue = stepper.DoubleValue;
        stepper.Activated += stepped;
        Bindings.Own(new ActionDisposable(() => stepper.Activated -= stepped));
        _form.KeyViews.Add(stepper);
        var concurrency = grid.Row(Translator.ImapSetup_ConcurrencyTitle, WinoLayout.HStack(WinoStyle.Space2, count, stepper));
        var concurrencyHelp = grid.Row((NSTextField?)null, FormGrid.Help(Translator.ImapSetup_ConcurrencyDescription));
        _form.Visible(concurrency, nameof(vm.IsMailAdvancedOptionsVisible), s => s.IsMailAdvancedOptionsVisible);
        _form.Visible(concurrencyHelp, nameof(vm.IsMailAdvancedOptionsVisible), s => s.IsMailAdvancedOptionsVisible);

        var append = grid.Row((NSTextField?)null, _form.Check(Translator.SettingsAccountManagementAppendMessage_Title,
            nameof(vm.ShouldAppendMessagesToSentFolder), s => s.ShouldAppendMessagesToSentFolder, (s, v) => s.ShouldAppendMessagesToSentFolder = v));
        var appendHelp = grid.Row((NSTextField?)null, FormGrid.Help(Translator.SettingsAccountManagementAppendMessage_Description));
        _form.Visible(append, nameof(vm.IsMailAdvancedOptionsVisible), s => s.IsMailAdvancedOptionsVisible);
        _form.Visible(appendHelp, nameof(vm.IsMailAdvancedOptionsVisible), s => s.IsMailAdvancedOptionsVisible);

        expander.Add(grid);
        return expander;
    }

    #endregion

    private WinoSettingsExpander Expander(string header, WinoIconGlyph icon, NSView? status,
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

    /// <summary>Host field and a narrow port field on one row, as in Mail's account settings.</summary>
    private static NSView HostAndPort(NSTextField host, NSTextField port)
    {
        port.WidthAnchor.ConstraintEqualTo(72).Active = true;
        foreach (var constraint in port.Constraints.Where(c => c.FirstAttribute == NSLayoutAttribute.Width && c.Relation == NSLayoutRelation.GreaterThanOrEqual))
            constraint.Active = false;
        port.SetContentHuggingPriorityForOrientation(750, NSLayoutConstraintOrientation.Horizontal);
        if (string.IsNullOrEmpty(port.PlaceholderString)) port.PlaceholderString = Translator.IMAPSetupDialog_IncomingMailServerPort;
        var row = WinoLayout.HStack(WinoStyle.Space2, host, port);
        row.Alignment = NSLayoutAttribute.FirstBaseline;
        return row;
    }

    private static NSView Column(params NSView[] views)
    {
        var stack = WinoLayout.VStack(WinoStyle.Space3, views);
        stack.Alignment = NSLayoutAttribute.Leading;
        foreach (var view in views) view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        return stack;
    }

    protected override Task InitializeAsync(NavigationMode mode, object? parameter)
    {
#if DEBUG
        // Debug bridge: imap-step signin|servers switches the guided step without validating or
        // discovering; imap-expand on|off opens every server section. Nothing is typed.
        MacDebugBridge.Register("imap-step", async args =>
        {
            var step = args.Length > 0 && args[0].Equals("servers", StringComparison.OrdinalIgnoreCase) ? ImapSetupStep.Servers : ImapSetupStep.SignIn;
            await Dispatcher.ExecuteOnUIThread(() => ViewModel.CurrentSetupStep = step);
            return "ok " + step;
        });
        MacDebugBridge.Register("imap-expand", async args =>
        {
            bool expand = args.Length == 0 || !args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
            await Dispatcher.ExecuteOnUIThread(() =>
                ViewModel.IsIncomingExpanded = ViewModel.IsOutgoingExpanded = ViewModel.IsCalDavExpanded = ViewModel.IsCardDavExpanded = expand);
            return "ok";
        });
#endif
        return ViewModel.InitializeAsync(mode, parameter!);
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception) { ReportError(exception); }
    }

    public override void ViewDidAppear()
    {
        base.ViewDidAppear();
        OnboardingPageView.ChainKeyViews(_form.KeyViews, _primary);
    }
}
