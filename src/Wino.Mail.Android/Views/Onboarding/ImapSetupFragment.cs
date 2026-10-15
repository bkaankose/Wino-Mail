using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Android.Bindings;

namespace Wino.Mail.Android.Views.Onboarding;

/// <summary>
/// Other (IMAP) account (canvas Material pass board ImapSetup; Windows ImapCalDavSettingsPage in its add-account flow):
/// the shared two-step setup on one page. Sign-in: name, address and password. Continue looks up the servers; the
/// servers step shows the incoming and outgoing servers (and CalDAV and CardDAV when the account uses them), each with
/// the shared validation errors inline. The top bar's Advanced action reveals the shared advanced options (proxy,
/// connection limit, copy of sent mail). The primary action follows the ViewModel (Continue, then Add account), which
/// validates the connection and hands over to account setup.
/// </summary>
public sealed class ImapSetupFragment : WinoFragment<ImapCalDavSettingsPageViewModel>
{
    private const string AdvancedStateKey = "ImapSetup.Advanced";

    protected override int LayoutResource => Resource.Layout.onboarding_page;

    protected override void OnBind(View view, ImapCalDavSettingsPageViewModel viewModel, IDispatcher dispatcher)
    {
        var binder = new ViewBinder(Bindings, dispatcher, ReportError);
        WinoInsets.ApplyWithKeyboard(view.FindViewById(Resource.Id.onboarding_insets) ?? view);
        var toolbar = OnboardingViews.Toolbar(view, binder, () => Run(viewModel.BackCommand));
        // Back returns from the servers to the sign-in step, then to the provider page (the ViewModel's Back).
        OnboardingViews.InterceptBack(this, binder, viewModel, nameof(viewModel.IsBusy), _ => true, () => Run(viewModel.BackCommand));
        if (toolbar is not null)
        {
            binder.BindAny(viewModel, vm => vm.IsSignInStepVisible ? vm.SignInTitle : Translator.ImapCalDavSettingsPage_TitleCreate,
                title => toolbar.SetHeading(title));
        }

        if (view.FindViewById<LinearLayout>(Resource.Id.onboarding_form) is not { Context: { } context } root) return;
        var form = new OnboardingForm<ImapCalDavSettingsPageViewModel>(context, root, binder, viewModel);

        var pageError = form.InfoBar(WinoInfoBarSeverity.Error);
        pageError.IsClosable = true;
        binder.BindAny(viewModel, vm => (vm.IsPageInfoBarOpen, vm.PageInfoBarTitle, vm.PageInfoBarMessage), state =>
        {
            pageError.Title = state.PageInfoBarTitle;
            pageError.Message = state.PageInfoBarMessage;
            pageError.IsOpen = state.IsPageInfoBarOpen;
        });
        EventHandler<WinoInfoBarClosedEventArgs> closed = (_, _) => Run(viewModel.HidePageErrorCommand);
        pageError.Closed += closed;
        binder.Scope.Own(new Wino.Presentation.Android.Bindings.ActionDisposable(() => pageError.Closed -= closed));

        form.Group(() => BuildSignIn(form, viewModel), nameof(viewModel.IsSignInStepVisible), vm => vm.IsSignInStepVisible);
        var advanced = (Entry?.State.TryGetValue(AdvancedStateKey, out var stored) == true && stored is AdvancedState state) ? state : new AdvancedState();
        if (Entry is { } entry) entry.State[AdvancedStateKey] = advanced;
        LinearLayout? advancedGroup = null;
        form.Group(() =>
        {
            BuildServers(form, viewModel);
            advancedGroup = form.Group(() => BuildAdvanced(form, viewModel));
        }, nameof(viewModel.IsServersStepVisible), vm => vm.IsServersStepVisible);
        OnboardingViews.Show(advancedGroup, advanced.IsShown);

        if (toolbar?.Menu is { } menu)
        {
            var item = menu.Add(Translator.ImapSetup_AdvancedTitle);
            item?.SetShowAsAction(ShowAsAction.Always);
            item?.SetCheckable(true);
            item?.SetChecked(advanced.IsShown);
            binder.Bind(viewModel, nameof(viewModel.IsServersStepVisible), vm => vm.IsServersStepVisible, visible => item?.SetVisible(visible));
            EventHandler<AndroidX.AppCompat.Widget.Toolbar.MenuItemClickEventArgs> clicked = (_, _) =>
            {
                advanced.IsShown = !advanced.IsShown;
                item?.SetChecked(advanced.IsShown);
                OnboardingViews.Show(advancedGroup, advanced.IsShown);
                if (advanced.IsShown && advancedGroup is not null && view.FindViewById<AndroidX.Core.Widget.NestedScrollView>(Resource.Id.onboarding_scroll) is { } scroll)
                    scroll.Post(() => scroll.SmoothScrollTo(0, advancedGroup.Top));
            };
            toolbar.MenuItemClick += clicked;
            binder.Scope.Own(new Wino.Presentation.Android.Bindings.ActionDisposable(() => toolbar.MenuItemClick -= clicked));
        }

        if (view.FindViewById(Resource.Id.onboarding_busy) is { } busy)
            binder.Visible(busy, viewModel, nameof(viewModel.IsBusy), vm => vm.IsBusy);
        if (view.FindViewById<TextView>(Resource.Id.onboarding_busy_text) is { } busyText)
            binder.BindAny(viewModel, vm => vm.IsBusy ? vm.BusyText : string.Empty, text => OnboardingViews.Caption(busyText, text));
        if (view.FindViewById<MaterialButton>(Resource.Id.onboarding_secondary) is { } back)
        {
            back.Text = Translator.Buttons_Back;
            binder.Command(back, viewModel.BackCommand);
        }
        if (view.FindViewById<MaterialButton>(Resource.Id.onboarding_primary) is { } primary)
        {
            binder.Bind(viewModel, nameof(viewModel.PrimaryActionText), vm => vm.PrimaryActionText, text => primary.Text = text);
            binder.Command(primary, viewModel.PrimaryActionCommand);
        }
        if (view.FindViewById<AndroidX.Core.Widget.NestedScrollView>(Resource.Id.onboarding_scroll) is { } formScroll)
            binder.Bind(viewModel, nameof(viewModel.CurrentSetupStep), vm => vm.CurrentSetupStep, _ => formScroll.ScrollTo(0, 0));
    }

    private static void BuildSignIn(OnboardingForm<ImapCalDavSettingsPageViewModel> form, ImapCalDavSettingsPageViewModel viewModel)
    {
        var binder = form.Binder;
        form.Help(viewModel.SignInDescription, nameof(viewModel.SignInDescription), vm => vm.SignInDescription);
        form.Group(() => form.Field(Translator.ImapSetup_YourName, nameof(viewModel.DisplayName), vm => vm.DisplayName, (vm, value) => vm.DisplayName = value,
                InputTypes.ClassText | InputTypes.TextFlagCapWords, Translator.ImapSetup_YourNameDescription),
            nameof(viewModel.IsSignInSenderNameVisible), vm => vm.IsSignInSenderNameVisible);
        var address = form.Field(Translator.IMAPSetupDialog_MailAddress, nameof(viewModel.EmailAddress), vm => vm.EmailAddress, (vm, value) => vm.EmailAddress = value,
            InputTypes.ClassText | InputTypes.TextVariationEmailAddress);
        address.PlaceholderText = Translator.IMAPSetupDialog_MailAddressPlaceholder;
        binder.Bind(viewModel, nameof(viewModel.SignInAddressHeader), vm => vm.SignInAddressHeader, header => address.Hint = header);
        form.Group(() => form.Field(Translator.ImapSetup_ServerAddressOptional, nameof(viewModel.CalDavServiceUrl), vm => vm.CalDavServiceUrl,
                (vm, value) => vm.CalDavServiceUrl = value, InputTypes.ClassText | InputTypes.TextVariationUri, Translator.ImapSetup_ServerAddressPlaceholder),
            nameof(viewModel.IsSignInServerAddressVisible), vm => vm.IsSignInServerAddressVisible);
        form.Field(Translator.IMAPSetupDialog_Password, nameof(viewModel.Password), vm => vm.Password, (vm, value) => vm.Password = value,
            InputTypes.ClassText | InputTypes.TextVariationPassword);
        form.Help(property: nameof(viewModel.AppPasswordHelpText), read: vm => vm.AppPasswordHelpText);
        var link = form.Link(Translator.ImapSetup_AppPasswordHelpLink, viewModel.OpenAppPasswordHelpCommand);
        binder.Visible(link, viewModel, nameof(viewModel.HasAppPasswordHelpLink), vm => vm.HasAppPasswordHelpLink);
    }

    private static void BuildServers(OnboardingForm<ImapCalDavSettingsPageViewModel> form, ImapCalDavSettingsPageViewModel viewModel)
    {
        var binder = form.Binder;
        var found = form.InfoBar(WinoInfoBarSeverity.Success);
        binder.BindAny(viewModel, vm => (vm.IsDiscoveryFoundVisible, vm.DiscoveryFoundMessage), state =>
        {
            found.Message = state.DiscoveryFoundMessage;
            found.IsOpen = state.IsDiscoveryFoundVisible;
        });
        var notFound = form.InfoBar(WinoInfoBarSeverity.Warning);
        binder.BindAny(viewModel, vm => (vm.IsDiscoveryNotFoundVisible, vm.DiscoveryNotFoundMessage), state =>
        {
            notFound.Message = state.DiscoveryNotFoundMessage;
            notFound.IsOpen = state.IsDiscoveryNotFoundVisible;
        });

        var security = viewModel.AvailableConnectionSecurityDisplayNames;
        var authentication = viewModel.AvailableAuthenticationMethodDisplayNames.ToList();

        form.Group(() =>
        {
            var title = form.Section(viewModel.IncomingSettingsTitle);
            binder.Bind(viewModel, nameof(viewModel.IncomingSettingsTitle), vm => vm.IncomingSettingsTitle, text => title.Text = text);
            ConnectionError(form, viewModel, nameof(viewModel.HasMailConnectionError), vm => vm.HasMailConnectionError, nameof(viewModel.MailConnectionError), vm => vm.MailConnectionError);
            HostAndPort(form, Translator.ImapSetup_ServerHeader, nameof(viewModel.IncomingServer), vm => vm.IncomingServer, (vm, v) => vm.IncomingServer = v,
                Translator.IMAPSetupDialog_IncomingMailServerPort, nameof(viewModel.IncomingServerPort), vm => vm.IncomingServerPort, (vm, v) => vm.IncomingServerPort = v);
            form.Dropdown(Translator.ImapCalDavSettingsPage_ConnectionSecurityHeader, security, nameof(viewModel.SelectedIncomingServerConnectionSecurityIndex),
                vm => vm.SelectedIncomingServerConnectionSecurityIndex, (vm, i) => vm.SelectedIncomingServerConnectionSecurityIndex = i);
            form.Dropdown(Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader, authentication, nameof(viewModel.SelectedIncomingServerAuthenticationMethodIndex),
                vm => vm.SelectedIncomingServerAuthenticationMethodIndex, (vm, i) => vm.SelectedIncomingServerAuthenticationMethodIndex = i);
            form.Field(Translator.IMAPSetupDialog_Username, nameof(viewModel.IncomingServerUsername), vm => vm.IncomingServerUsername, (vm, v) => vm.IncomingServerUsername = v);
            form.Field(Translator.IMAPSetupDialog_Password, nameof(viewModel.IncomingServerPassword), vm => vm.IncomingServerPassword, (vm, v) => vm.IncomingServerPassword = v,
                InputTypes.ClassText | InputTypes.TextVariationPassword);

            form.Section(Translator.ImapSetup_OutgoingTitle);
            HostAndPort(form, Translator.ImapSetup_ServerHeader, nameof(viewModel.OutgoingServer), vm => vm.OutgoingServer, (vm, v) => vm.OutgoingServer = v,
                Translator.IMAPSetupDialog_OutgoingMailServerPort, nameof(viewModel.OutgoingServerPort), vm => vm.OutgoingServerPort, (vm, v) => vm.OutgoingServerPort = v);
            form.Dropdown(Translator.ImapCalDavSettingsPage_ConnectionSecurityHeader, security, nameof(viewModel.SelectedOutgoingServerConnectionSecurityIndex),
                vm => vm.SelectedOutgoingServerConnectionSecurityIndex, (vm, i) => vm.SelectedOutgoingServerConnectionSecurityIndex = i);
            form.Dropdown(Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader, authentication, nameof(viewModel.SelectedOutgoingServerAuthenticationMethodIndex),
                vm => vm.SelectedOutgoingServerAuthenticationMethodIndex, (vm, i) => vm.SelectedOutgoingServerAuthenticationMethodIndex = i);
            form.Switch(Translator.ImapSetup_UseIncomingSignInForOutgoing, nameof(viewModel.UseIncomingCredentialsForOutgoing),
                vm => vm.UseIncomingCredentialsForOutgoing, (vm, v) => vm.UseIncomingCredentialsForOutgoing = v);
            form.Group(() =>
            {
                form.Field(Translator.IMAPSetupDialog_OutgoingMailServerUsername, nameof(viewModel.OutgoingServerUsername), vm => vm.OutgoingServerUsername, (vm, v) => vm.OutgoingServerUsername = v);
                form.Field(Translator.IMAPSetupDialog_OutgoingMailServerPassword, nameof(viewModel.OutgoingServerPassword), vm => vm.OutgoingServerPassword, (vm, v) => vm.OutgoingServerPassword = v,
                    InputTypes.ClassText | InputTypes.TextVariationPassword);
            }, nameof(viewModel.IsOutgoingCredentialsVisible), vm => vm.IsOutgoingCredentialsVisible);
        }, nameof(viewModel.IsMailSupportEnabled), vm => vm.IsMailSupportEnabled);

        form.Group(() =>
        {
            form.Section(Translator.ImapSetup_CalDavTitle);
            ConnectionError(form, viewModel, nameof(viewModel.HasCalDavConnectionError), vm => vm.HasCalDavConnectionError, nameof(viewModel.CalDavConnectionError), vm => vm.CalDavConnectionError);
            form.Field(Translator.ImapCalDavSettingsPage_CalDavServiceUrl, nameof(viewModel.CalDavServiceUrl), vm => vm.CalDavServiceUrl, (vm, v) => vm.CalDavServiceUrl = v,
                InputTypes.ClassText | InputTypes.TextVariationUri);
            DavCredentials(form, viewModel, nameof(viewModel.IsDavCredentialsOptionVisible), vm => vm.IsDavCredentialsOptionVisible,
                nameof(viewModel.IsDavCredentialsVisible), vm => vm.IsDavCredentialsVisible);
        }, nameof(viewModel.IsCalDavSettingsVisible), vm => vm.IsCalDavSettingsVisible);

        form.Group(() =>
        {
            form.Section(Translator.ImapSetup_CardDavTitle);
            form.Field(Translator.ImapCalDavSettingsPage_CardDavServiceUrl, nameof(viewModel.CardDavServiceUrl), vm => vm.CardDavServiceUrl, (vm, v) => vm.CardDavServiceUrl = v,
                InputTypes.ClassText | InputTypes.TextVariationUri, Translator.ImapSetup_CardDavSharedSignIn).PlaceholderText = Translator.ImapCalDavSettingsPage_CardDavDiscoveryPlaceholder;
            DavCredentials(form, viewModel, nameof(viewModel.IsCardDavCredentialsOptionVisible), vm => vm.IsCardDavCredentialsOptionVisible,
                nameof(viewModel.IsCardDavCredentialsVisible), vm => vm.IsCardDavCredentialsVisible);
        }, nameof(viewModel.IsCardDavSettingsVisible), vm => vm.IsCardDavSettingsVisible);

        form.Link(Translator.ImapSetup_TestConnections, viewModel.TestConnectionsCommand);
    }

    private static void BuildAdvanced(OnboardingForm<ImapCalDavSettingsPageViewModel> form, ImapCalDavSettingsPageViewModel viewModel)
    {
        form.Section(Translator.ImapSetup_AdvancedTitle);
        form.Help(Translator.ImapSetup_AdvancedDescription, topDp: 4);
        HostAndPort(form, Translator.ImapSetup_ProxyTitle, nameof(viewModel.ProxyServer), vm => vm.ProxyServer, (vm, v) => vm.ProxyServer = v,
            Translator.IMAPSetupDialog_IncomingMailServerPort, nameof(viewModel.ProxyServerPort), vm => vm.ProxyServerPort, (vm, v) => vm.ProxyServerPort = v);
        form.Help(Translator.ImapSetup_ProxyDescription);
        form.Group(() =>
        {
            var limits = Enumerable.Range(1, 20).Select(value => value.ToString(System.Globalization.CultureInfo.CurrentCulture)).ToList();
            form.Dropdown(Translator.ImapSetup_ConcurrencyTitle, limits, nameof(viewModel.MaxConcurrentClients),
                vm => vm.MaxConcurrentClients - 1, (vm, index) => vm.MaxConcurrentClientsValue = index + 1);
            form.Help(Translator.ImapSetup_ConcurrencyDescription);
            form.Switch(Translator.SettingsAccountManagementAppendMessage_Title, nameof(viewModel.ShouldAppendMessagesToSentFolder),
                vm => vm.ShouldAppendMessagesToSentFolder, (vm, v) => vm.ShouldAppendMessagesToSentFolder = v);
            form.Help(Translator.SettingsAccountManagementAppendMessage_Description);
        }, nameof(viewModel.IsMailAdvancedOptionsVisible), vm => vm.IsMailAdvancedOptionsVisible);
    }

    private static void HostAndPort(OnboardingForm<ImapCalDavSettingsPageViewModel> form,
        string hostLabel, string hostProperty, Func<ImapCalDavSettingsPageViewModel, string?> readHost, Action<ImapCalDavSettingsPageViewModel, string> writeHost,
        string portLabel, string portProperty, Func<ImapCalDavSettingsPageViewModel, string?> readPort, Action<ImapCalDavSettingsPageViewModel, string> writePort)
    {
        form.Row(() =>
        {
            form.Field(hostLabel, hostProperty, readHost, writeHost, InputTypes.ClassText | InputTypes.TextVariationUri);
            form.Field(portLabel, portProperty, readPort, writePort, InputTypes.ClassNumber, fixedWidthDimension: Resource.Dimension.onboarding_port_width);
        });
    }

    private static void ConnectionError(OnboardingForm<ImapCalDavSettingsPageViewModel> form, ImapCalDavSettingsPageViewModel viewModel,
        string visibleProperty, Func<ImapCalDavSettingsPageViewModel, bool> visible, string messageProperty, Func<ImapCalDavSettingsPageViewModel, string> message)
    {
        var error = form.InfoBar(WinoInfoBarSeverity.Error, Translator.IMAPSetupDialog_ValidationFailed_Title);
        form.Binder.Bind(viewModel, messageProperty, message, text => error.Message = text);
        form.Binder.Bind(viewModel, visibleProperty, visible, open => error.IsOpen = open);
    }

    private static void DavCredentials(OnboardingForm<ImapCalDavSettingsPageViewModel> form, ImapCalDavSettingsPageViewModel viewModel,
        string optionProperty, Func<ImapCalDavSettingsPageViewModel, bool> optionVisible, string credentialsProperty, Func<ImapCalDavSettingsPageViewModel, bool> credentialsVisible)
    {
        form.Group(() => form.Switch(Translator.ImapSetup_UseMailSignInForDav, nameof(viewModel.UseMailCredentialsForDav),
            vm => vm.UseMailCredentialsForDav, (vm, v) => vm.UseMailCredentialsForDav = v), optionProperty, optionVisible);
        form.Group(() =>
        {
            form.Field(Translator.ImapCalDavSettingsPage_DavUsername, nameof(viewModel.CalDavUsername), vm => vm.CalDavUsername, (vm, v) => vm.CalDavUsername = v);
            form.Field(Translator.ImapCalDavSettingsPage_DavPassword, nameof(viewModel.CalDavPassword), vm => vm.CalDavPassword, (vm, v) => vm.CalDavPassword = v,
                InputTypes.ClassText | InputTypes.TextVariationPassword);
        }, credentialsProperty, credentialsVisible);
    }

    private void Run(System.Windows.Input.ICommand command)
    {
        try { if (command.CanExecute(null)) command.Execute(null); }
        catch (Exception exception) { ReportError(exception); }
    }

    /// <summary>Whether the Advanced section is open; kept with the page entry so rotation keeps it.</summary>
    private sealed class AdvancedState
    {
        public bool IsShown { get; set; }
    }
}
