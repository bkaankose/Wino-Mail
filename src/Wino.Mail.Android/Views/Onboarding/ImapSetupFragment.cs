using Android.Text;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Android;
using Wino.Presentation.Android.Bindings;

namespace Wino.Mail.Android.Views.Onboarding;

/// <summary>
/// Other (IMAP) account (canvas Material pass board ImapSetup; Windows ImapCalDavSettingsPage): the shared two-step
/// setup on one page. Sign-in: name and address first; Continue looks up the servers and the secure sign-in they
/// publish, then the method stage offers that sign-in in a Custom Tab, or the password. The servers step shows the
/// incoming and outgoing servers (and CalDAV and CardDAV when the account uses them), each with the shared validation
/// errors inline. The top bar's Advanced action reveals the shared advanced options (proxy, connection limit, copy of
/// sent mail). The primary action follows the ViewModel (Continue, Next, then Add account), which validates the
/// connection and hands over to account setup. Editing an account shows its sign-in (a password or "Sign in again")
/// above the servers.
/// </summary>
public sealed class ImapSetupFragment : WinoFragment<ImapCalDavSettingsPageViewModel>
{
    private const string AdvancedStateKey = "ImapSetup.Advanced";

    protected override int LayoutResource => Resource.Layout.onboarding_page;

    protected override void OnBind(View view, ImapCalDavSettingsPageViewModel viewModel, IDispatcher dispatcher)
    {
#if DEBUG
        global::Wino.Mail.Android.Views.Debug.DebugOnboarding.Track(viewModel);
#endif
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

        var oauthNotice = form.InfoBar(WinoInfoBarSeverity.Informational);
        oauthNotice.IsClosable = true;
        binder.BindAny(viewModel, vm => (vm.IsOAuthInfoBarOpen, vm.OAuthInfoBarMessage, vm.OAuthInfoBarType), state =>
        {
            oauthNotice.Severity = state.OAuthInfoBarType switch
            {
                InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
                InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
                InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
                _ => WinoInfoBarSeverity.Informational
            };
            oauthNotice.Message = state.OAuthInfoBarMessage;
            oauthNotice.IsOpen = state.IsOAuthInfoBarOpen;
        });
        EventHandler<WinoInfoBarClosedEventArgs> oauthClosed = (_, _) => Run(viewModel.HideOAuthInfoBarCommand);
        oauthNotice.Closed += oauthClosed;
        binder.Scope.Own(new Wino.Presentation.Android.Bindings.ActionDisposable(() => oauthNotice.Closed -= oauthClosed));

        form.Group(() => BuildSignIn(form, viewModel), nameof(viewModel.IsSignInStepVisible), vm => vm.IsSignInStepVisible);
        form.Group(() => BuildEditSignIn(form, viewModel), nameof(viewModel.IsEditSignInVisible), vm => vm.IsEditSignInVisible);
        var advanced = (Entry?.State.TryGetValue(AdvancedStateKey, out var stored) == true && stored is AdvancedState state) ? state : new AdvancedState();
        if (Entry is { } entry) entry.State[AdvancedStateKey] = advanced;
        LinearLayout? advancedGroup = null;
        form.Group(() =>
        {
            BuildServers(form, viewModel);
            advancedGroup = form.Group(() => BuildAdvanced(form, viewModel));
        }, nameof(viewModel.IsServersStepVisible), vm => vm.IsServersStepVisible);
        OnboardingViews.Show(advancedGroup, advanced.IsShown);
        // Opening Advanced lists OAuth 2.0 among the authentication methods.
        if (advanced.IsShown) viewModel.IsAdvancedExpanded = true;

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
                viewModel.IsAdvancedExpanded = advanced.IsShown;
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
            // Until the user signs in on the secure sign-in card there is nothing to continue with.
            binder.BindAny(viewModel, vm => vm.IsPrimaryActionVisible, visible => OnboardingViews.Show(primary, visible));
        }
        if (view.FindViewById<AndroidX.Core.Widget.NestedScrollView>(Resource.Id.onboarding_scroll) is { } formScroll)
            binder.Bind(viewModel, nameof(viewModel.CurrentSetupStep), vm => vm.CurrentSetupStep, _ => formScroll.ScrollTo(0, 0));
    }

    /// <summary>
    /// Adding an account, first step. The address comes first: discovery decides whether the server offers secure
    /// sign-in before a password is asked for, then the method stage shows the browser sign-in or the password.
    /// </summary>
    private static void BuildSignIn(OnboardingForm<ImapCalDavSettingsPageViewModel> form, ImapCalDavSettingsPageViewModel viewModel)
    {
        form.GroupWhen(vm => vm.IsSignInIdentityStageVisible, () => BuildIdentityStage(form, viewModel));
        form.GroupWhen(vm => vm.IsSignInMethodStageVisible, () => BuildMethodStage(form, viewModel));
    }

    private static void BuildIdentityStage(OnboardingForm<ImapCalDavSettingsPageViewModel> form, ImapCalDavSettingsPageViewModel viewModel)
    {
        form.Help(viewModel.SignInDescription, nameof(viewModel.SignInDescription), vm => vm.SignInDescription);
        AddIdentityFields(form, viewModel);
        form.Group(() => form.Field(Translator.ImapSetup_ServerAddressOptional, nameof(viewModel.MailServerAddress), vm => vm.MailServerAddress,
                (vm, value) => vm.MailServerAddress = value, InputTypes.ClassText | InputTypes.TextVariationUri, Translator.ImapSetup_ServerAddressPlaceholder),
            nameof(viewModel.IsSignInMailServerAddressVisible), vm => vm.IsSignInMailServerAddressVisible);
        // Without mail there is nothing to discover from reliably, so the CalDAV address can be typed here.
        form.Group(() => form.Field(Translator.ImapSetup_ServerAddressOptional, nameof(viewModel.CalDavServiceUrl), vm => vm.CalDavServiceUrl,
                (vm, value) => vm.CalDavServiceUrl = value, InputTypes.ClassText | InputTypes.TextVariationUri, Translator.ImapSetup_ServerAddressPlaceholder),
            nameof(viewModel.IsSignInServerAddressVisible), vm => vm.IsSignInServerAddressVisible);
        var exchange = form.Link(Translator.ExchangeSetup_OpenFromImap, viewModel.OpenExchangeSetupCommand);
        form.Binder.Visible(exchange, viewModel, nameof(viewModel.IsCreateMode), vm => vm.IsCreateMode);
    }

    private static void AddIdentityFields(OnboardingForm<ImapCalDavSettingsPageViewModel> form, ImapCalDavSettingsPageViewModel viewModel)
    {
        form.Group(() => form.Field(Translator.ImapSetup_YourName, nameof(viewModel.DisplayName), vm => vm.DisplayName, (vm, value) => vm.DisplayName = value,
                InputTypes.ClassText | InputTypes.TextFlagCapWords, Translator.ImapSetup_YourNameDescription),
            nameof(viewModel.IsSignInSenderNameVisible), vm => vm.IsSignInSenderNameVisible);
        var address = form.Field(Translator.IMAPSetupDialog_MailAddress, nameof(viewModel.EmailAddress), vm => vm.EmailAddress, (vm, value) => vm.EmailAddress = value,
            InputTypes.ClassText | InputTypes.TextVariationEmailAddress);
        address.PlaceholderText = Translator.IMAPSetupDialog_MailAddressPlaceholder;
        form.Binder.Bind(viewModel, nameof(viewModel.SignInAddressHeader), vm => vm.SignInAddressHeader, header => address.Hint = header);
    }

    private static void AddPasswordFields(OnboardingForm<ImapCalDavSettingsPageViewModel> form, ImapCalDavSettingsPageViewModel viewModel)
    {
        form.Field(Translator.IMAPSetupDialog_Password, nameof(viewModel.Password), vm => vm.Password, (vm, value) => vm.Password = value,
            InputTypes.ClassText | InputTypes.TextVariationPassword);
        form.Help(property: nameof(viewModel.AppPasswordHelpText), read: vm => vm.AppPasswordHelpText);
        var link = form.Link(Translator.ImapSetup_AppPasswordHelpLink, viewModel.OpenAppPasswordHelpCommand);
        form.Binder.Visible(link, viewModel, nameof(viewModel.HasAppPasswordHelpLink), vm => vm.HasAppPasswordHelpLink);
    }

    /// <summary>How to sign in, once discovery has run: the browser sign-in the server offers, or the password.</summary>
    private static void BuildMethodStage(OnboardingForm<ImapCalDavSettingsPageViewModel> form, ImapCalDavSettingsPageViewModel viewModel)
    {
        form.Row(() =>
        {
            var summary = form.Body(nameof(viewModel.SignInAddressSummary), vm => vm.SignInAddressSummary);
            summary.SetMaxLines(1);
            summary.Ellipsize = TextUtils.TruncateAt.Middle;
            if (summary.LayoutParameters is LinearLayout.LayoutParams parameters)
            {
                parameters.Width = 0;
                parameters.Weight = 1;
                parameters.Gravity = GravityFlags.CenterVertical;
                parameters.TopMargin = 0;
            }
            var change = form.Link(Translator.ImapSetup_ChangeAddress, viewModel.ChangeAddressCommand);
            if (change.LayoutParameters is LinearLayout.LayoutParams changeParameters) changeParameters.Gravity = GravityFlags.CenterVertical;
        });

        // The server offers secure sign-in: the Custom Tab opens only when the user taps.
        form.GroupWhen(vm => vm.IsOAuthChoiceVisible, () =>
        {
            form.Card(WinoIconGlyph.LockClosed, WinoColors.Accent(form.Context), () =>
            {
                form.Body(nameof(viewModel.SecureSignInTitle), vm => vm.SecureSignInTitle, strong: true);
                form.Help(Translator.ImapSetup_SecureSignInDescription, topDp: 4);
                form.GroupWhen(vm => vm.IsIssuerConfirmationVisible, () =>
                {
                    var confirm = form.Switch(string.Empty, nameof(viewModel.IsIssuerConfirmed), vm => vm.IsIssuerConfirmed, (vm, v) => vm.IsIssuerConfirmed = v);
                    form.Binder.Bind(viewModel, nameof(viewModel.IssuerConfirmationText), vm => vm.IssuerConfirmationText, text =>
                    {
                        if (confirm.FindViewById<TextView>(Resource.Id.switch_label) is { } label) label.Text = text;
                        if (confirm.FindViewById(Resource.Id.switch_toggle) is { } toggle) toggle.ContentDescription = text;
                    });
                    form.Help(Translator.ImapSetup_OAuthConfirmIssuerDescription, topDp: 4);
                });
                form.Row(() =>
                {
                    var signIn = form.Button(string.Empty, viewModel.SignInWithOAuthCommand, primary: true);
                    form.Binder.Bind(viewModel, nameof(viewModel.OAuthSignInButtonText), vm => vm.OAuthSignInButtonText, text => signIn.Text = text);
                    form.Spinner(nameof(viewModel.IsOAuthSigningIn), vm => vm.IsOAuthSigningIn);
                }, topDp: 8);
                form.Help(property: nameof(viewModel.OAuthIssuerHost), read: vm => vm.OAuthIssuerHost);
            });
            form.Link(Translator.ImapSetup_UsePasswordInstead, viewModel.UsePasswordInsteadCommand);
        });

        // Signed in: the session stays in memory until the account is saved.
        form.GroupWhen(vm => vm.IsOAuthSignedInVisible, () =>
        {
            form.Card(WinoIconGlyph.CheckmarkCircle, WinoColors.Success(form.Context), () =>
            {
                form.Body(nameof(viewModel.OAuthSignedInTitle), vm => vm.OAuthSignedInTitle, strong: true);
                form.Help(property: nameof(viewModel.OAuthSignedInDescription), read: vm => vm.OAuthSignedInDescription, topDp: 4);
                form.Link(Translator.ImapSetup_OAuthSignInAgain, viewModel.SignInAgainCommand);
            });
            var noRefresh = form.InfoBar(WinoInfoBarSeverity.Warning);
            noRefresh.Message = Translator.ImapSetup_OAuthNoRefreshToken;
            form.Binder.BindAny(viewModel, vm => vm.IsNoRefreshTokenWarningVisible, open => noRefresh.IsOpen = open);
            var davScope = form.InfoBar(WinoInfoBarSeverity.Warning);
            davScope.Message = Translator.ImapSetup_OAuthDavScopeSignInRequired;
            form.Binder.BindAny(viewModel, vm => vm.IsDavScopeSignInRequiredForSetupVisible, open => davScope.IsOpen = open);
            form.Link(Translator.ImapSetup_UsePasswordInstead, viewModel.UsePasswordInsteadCommand);
        });

        form.GroupWhen(vm => vm.IsPasswordMethodVisible, () =>
        {
            AddPasswordFields(form, viewModel);
            form.ShowWhen(form.Link(Translator.ImapSetup_UseSecureSignInInstead, viewModel.UseSecureSignInInsteadCommand), vm => vm.IsUseSecureSignInVisible);
            form.ShowWhen(form.Link(Translator.ImapSetup_OrganizationSignIn, viewModel.ToggleOrganizationSignInCommand), vm => vm.IsOrganizationSignInOptionVisible);
        });

        // The server offers OAuth but Wino has no client for it: the organization registers one.
        form.GroupWhen(vm => vm.IsOrganizationSignInFieldsVisible, () =>
        {
            form.Help(Translator.ImapSetup_OrganizationSignInDescription);
            form.Mono(viewModel.OAuthCustomSchemeRedirectUri);
            form.Mono(viewModel.OAuthLoopbackRedirectUri);
            form.Field(Translator.ImapSetup_OAuthIssuerHeader, nameof(viewModel.CustomOAuthIssuer), vm => vm.CustomOAuthIssuer, (vm, v) => vm.CustomOAuthIssuer = v,
                InputTypes.ClassText | InputTypes.TextVariationUri).PlaceholderText = Translator.ImapSetup_OAuthIssuerPlaceholder;
            form.Field(Translator.ImapSetup_OAuthClientIdHeader, nameof(viewModel.CustomOAuthClientId), vm => vm.CustomOAuthClientId, (vm, v) => vm.CustomOAuthClientId = v);
            form.Field(Translator.ImapSetup_OAuthScopesHeader, nameof(viewModel.CustomOAuthScopes), vm => vm.CustomOAuthScopes, (vm, v) => vm.CustomOAuthScopes = v)
                .PlaceholderText = Translator.ImapSetup_OAuthScopesPlaceholder;
            form.Row(() =>
            {
                form.Button(Translator.ImapSetup_OAuthSignInWithClient, viewModel.SignInWithOrganizationClientCommand);
                form.Spinner(nameof(viewModel.IsOAuthSigningIn), vm => vm.IsOAuthSigningIn);
            }, topDp: 8);
        });
    }

    /// <summary>
    /// Editing an account: the sign-in shared by every server, a password or the secure sign-in. "Sign in again" also
    /// serves accounts restored from a backup, whose tokens stay on the device that signed in; a stored sign-in this
    /// device cannot open explains that instead.
    /// </summary>
    private static void BuildEditSignIn(OnboardingForm<ImapCalDavSettingsPageViewModel> form, ImapCalDavSettingsPageViewModel viewModel)
    {
        form.Section(Translator.ImapSetup_SignInGroup);
        AddIdentityFields(form, viewModel);
        form.GroupWhen(vm => vm.IsEditPasswordSignInVisible, () => AddPasswordFields(form, viewModel));
        form.GroupWhen(vm => vm.IsEditOAuthSignInVisible, () =>
        {
            form.Row(() =>
            {
                form.Button(Translator.ImapSetup_OAuthSignInAgain, viewModel.ReauthenticateOAuthCommand);
                form.Spinner(nameof(viewModel.IsOAuthSigningIn), vm => vm.IsOAuthSigningIn);
            }, topDp: 8);
            form.Help(property: nameof(viewModel.EditOAuthDescription), read: vm => vm.EditOAuthDescription);
        });
        var davScope = form.InfoBar(WinoInfoBarSeverity.Warning);
        davScope.Message = Translator.ImapSetup_OAuthDavScopeSignInRequired;
        form.Binder.BindAny(viewModel, vm => vm.IsDavScopeSignInRequiredForEditVisible, open => davScope.IsOpen = open);
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
        var oauthServers = form.InfoBar(WinoInfoBarSeverity.Informational);
        binder.BindAny(viewModel, vm => (vm.IsOAuthServerSignInVisible, vm.OAuthServerSignInText), state =>
        {
            oauthServers.Message = state.OAuthServerSignInText;
            oauthServers.IsOpen = state.IsOAuthServerSignInVisible;
        });

        var security = viewModel.AvailableConnectionSecurityDisplayNames;

        form.Group(() =>
        {
            var title = form.Section(viewModel.IncomingSettingsTitle);
            binder.Bind(viewModel, nameof(viewModel.IncomingSettingsTitle), vm => vm.IncomingSettingsTitle, text => title.Text = text);
            ConnectionError(form, viewModel, nameof(viewModel.HasMailConnectionError), vm => vm.HasMailConnectionError, nameof(viewModel.MailConnectionError), vm => vm.MailConnectionError);
            HostAndPort(form, Translator.ImapSetup_ServerHeader, nameof(viewModel.IncomingServer), vm => vm.IncomingServer, (vm, v) => vm.IncomingServer = v,
                Translator.IMAPSetupDialog_IncomingMailServerPort, nameof(viewModel.IncomingServerPort), vm => vm.IncomingServerPort, (vm, v) => vm.IncomingServerPort = v);
            form.Dropdown(Translator.ImapCalDavSettingsPage_ConnectionSecurityHeader, security, nameof(viewModel.SelectedIncomingServerConnectionSecurityIndex),
                vm => vm.SelectedIncomingServerConnectionSecurityIndex, (vm, i) => vm.SelectedIncomingServerConnectionSecurityIndex = i);
            AuthenticationDropdown(form, viewModel, nameof(viewModel.SelectedIncomingServerAuthenticationMethodIndex),
                vm => vm.SelectedIncomingServerAuthenticationMethodIndex, (vm, i) => vm.SelectedIncomingServerAuthenticationMethodIndex = i);
            // The secure sign-in replaces the incoming user name and password.
            form.GroupWhen(vm => vm.IsIncomingPasswordSignInVisible, () =>
            {
                form.Field(Translator.IMAPSetupDialog_Username, nameof(viewModel.IncomingServerUsername), vm => vm.IncomingServerUsername, (vm, v) => vm.IncomingServerUsername = v);
                form.Field(Translator.IMAPSetupDialog_Password, nameof(viewModel.IncomingServerPassword), vm => vm.IncomingServerPassword, (vm, v) => vm.IncomingServerPassword = v,
                    InputTypes.ClassText | InputTypes.TextVariationPassword);
            });

            form.Section(Translator.ImapSetup_OutgoingTitle);
            HostAndPort(form, Translator.ImapSetup_ServerHeader, nameof(viewModel.OutgoingServer), vm => vm.OutgoingServer, (vm, v) => vm.OutgoingServer = v,
                Translator.IMAPSetupDialog_OutgoingMailServerPort, nameof(viewModel.OutgoingServerPort), vm => vm.OutgoingServerPort, (vm, v) => vm.OutgoingServerPort = v);
            form.Dropdown(Translator.ImapCalDavSettingsPage_ConnectionSecurityHeader, security, nameof(viewModel.SelectedOutgoingServerConnectionSecurityIndex),
                vm => vm.SelectedOutgoingServerConnectionSecurityIndex, (vm, i) => vm.SelectedOutgoingServerConnectionSecurityIndex = i);
            AuthenticationDropdown(form, viewModel, nameof(viewModel.SelectedOutgoingServerAuthenticationMethodIndex),
                vm => vm.SelectedOutgoingServerAuthenticationMethodIndex, (vm, i) => vm.SelectedOutgoingServerAuthenticationMethodIndex = i);
            form.ShowWhen(form.Switch(Translator.ImapSetup_UseIncomingSignInForOutgoing, nameof(viewModel.UseIncomingCredentialsForOutgoing),
                vm => vm.UseIncomingCredentialsForOutgoing, (vm, v) => vm.UseIncomingCredentialsForOutgoing = v), vm => vm.IsOutgoingSharedSignInOptionVisible);
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
            form.ShowWhen(form.Help(Translator.ImapSetup_DavUsesSecureSignIn), vm => vm.IsCalDavOAuthSelected);
            DavCredentials(form, viewModel, nameof(viewModel.IsDavCredentialsOptionVisible), vm => vm.IsDavCredentialsOptionVisible,
                nameof(viewModel.IsDavCredentialsVisible), vm => vm.IsDavCredentialsVisible);
        }, nameof(viewModel.IsCalDavSettingsVisible), vm => vm.IsCalDavSettingsVisible);

        form.Group(() =>
        {
            form.Section(Translator.ImapSetup_CardDavTitle);
            var cardDav = form.Field(Translator.ImapCalDavSettingsPage_CardDavServiceUrl, nameof(viewModel.CardDavServiceUrl), vm => vm.CardDavServiceUrl,
                (vm, v) => vm.CardDavServiceUrl = v, InputTypes.ClassText | InputTypes.TextVariationUri);
            cardDav.PlaceholderText = Translator.ImapCalDavSettingsPage_CardDavDiscoveryPlaceholder;
            binder.BindAny(viewModel, vm => vm.CardDavUrlDescription, text => cardDav.HelperText = string.IsNullOrEmpty(text) ? null : text);
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

    /// <summary>
    /// The authentication method list changes while the page is open (OAuth 2.0 joins it once discovery, a sign-in or
    /// Advanced offers it), so the menu reloads from the collection and reselects the current method.
    /// </summary>
    private static void AuthenticationDropdown(OnboardingForm<ImapCalDavSettingsPageViewModel> form, ImapCalDavSettingsPageViewModel viewModel,
        string property, Func<ImapCalDavSettingsPageViewModel, int> read, Action<ImapCalDavSettingsPageViewModel, int> write)
    {
        var methods = viewModel.AvailableAuthenticationMethodDisplayNames;
        var layout = form.Dropdown(Translator.ImapCalDavSettingsPage_AuthenticationMethodHeader, methods, property, read, write);
        if (layout.EditText is not Google.Android.Material.TextField.MaterialAutoCompleteTextView field) return;
        form.Binder.Collection(methods, () =>
        {
            field.SetSimpleItems(methods.ToArray());
            var index = read(viewModel);
            field.SetText(index >= 0 && index < methods.Count ? methods[index] : string.Empty, false);
        });
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
