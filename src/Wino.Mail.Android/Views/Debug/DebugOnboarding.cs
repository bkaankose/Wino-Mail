#if DEBUG
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authorization;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.Android.Views.Debug;

/// <summary>
/// Debug commands for the server setup page (the iOS OnboardingDiagnostics twin), so each secure sign-in
/// state renders on an emulator without a server: <c>imap</c> opens the custom server page as the wizard does,
/// <c>imapedit</c> opens it for the first IMAP account, <c>imapstage</c> and <c>imapoauth</c> (<c>--es kind</c>) move the open
/// page, and <c>oauthloopback</c> runs a
/// loopback OAuth sign-in against a public issuer so the redirect listener can be probed while the Custom Tab is in
/// front.
/// </summary>
internal static class DebugOnboarding
{
    private const int LoopbackProbePort = 47123;

    private static WeakReference<ImapCalDavSettingsPageViewModel>? _imap;

    public static void Track(ImapCalDavSettingsPageViewModel viewModel) => _imap = new(viewModel);

    public static async Task<string?> RunAsync(IServiceProvider services, AndroidNavigationService navigation, string command, string kind)
    {
        switch (command)
        {
            case "imap":
                var provider = services.GetRequiredService<IKnownImapProviderCatalog>().GetAvailableProviders()
                    .FirstOrDefault(item => item.Type == MailProviderType.IMAP4 && item.SpecialImapProvider == SpecialImapProvider.None);
                if (provider is null) return "no custom provider";
                var context = services.GetRequiredService<WelcomeWizardContext>();
                context.SelectedProvider = provider;
                context.AccountName ??= "IMAP";
                context.IsMailAccessEnabled = true;
                await navigation.NavigateAsync(WinoPage.ImapCalDavSettingsPage,
                    ImapCalDavSettingsNavigationContext.CreateForWizardMode(context.BuildAccountCreationDialogResult()));
                return "ok";
            case "imapedit":
                var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync();
                var account = accounts.FirstOrDefault(item => item.ProviderType is MailProviderType.IMAP4 or MailProviderType.POP3);
                if (account is null) return "no IMAP account";
                await navigation.NavigateAsync(WinoPage.ImapCalDavSettingsPage, ImapCalDavSettingsNavigationContext.CreateForEditMode(account.Id));
                return "ok";
            case "imapstage":
                return await OnImapAsync(services, vm =>
                {
                    vm.CurrentSetupStep = kind == "servers" ? ImapSetupStep.Servers : ImapSetupStep.SignIn;
                    vm.SignInStage = kind == "method" ? ImapSignInStage.Method : ImapSignInStage.Identity;
                });
            case "imapoauth":
                return await OnImapAsync(services, vm => FakeOAuthState(vm, string.IsNullOrEmpty(kind) ? "offered" : kind));
            case "oauthloopback":
                return await ProbeLoopbackAsync(services);
            default:
                return null;
        }
    }

    private static async Task<string> OnImapAsync(IServiceProvider services, Action<ImapCalDavSettingsPageViewModel> change)
    {
        if (_imap?.TryGetTarget(out var viewModel) != true || viewModel is null) return "no server page";
        await services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() => change(viewModel));
        return "ok";
    }

    /// <summary>Fakes what discovery or a stored sign-in left (private state, so reflection; Debug only).</summary>
    private static void FakeOAuthState(ImapCalDavSettingsPageViewModel vm, string mode)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(ImapCalDavSettingsPageViewModel);
        void Set(string field, object? value) => type.GetField(field, flags)?.SetValue(vm, value);
        var profile = new AccountOAuthProfile
        {
            Issuer = "https://auth.example.com",
            ClientId = "preview",
            RedirectKind = mode == "stored-loopback" ? OAuthRedirectKind.Loopback : OAuthRedirectKind.CustomScheme,
            TrustSource = OAuthTrustSource.Discovered
        };
        var offered = new OAuthDiscoveryResult { Profile = profile, IssuerHost = "auth.example.com" };
        switch (mode)
        {
            case "org":
                Set("_oauthDiscovery", new OAuthDiscoveryResult { OfferedWithoutClient = true, IssuerHost = "auth.example.com" });
                break;
            case "confirm":
                Set("_oauthDiscovery", new OAuthDiscoveryResult { Profile = profile, IssuerHost = "auth.example.com", RequiresIssuerConfirmation = true });
                break;
            case "signedin":
                Set("_oauthDiscovery", offered);
                Set("_oauthSession", new OAuthSetupSession(Guid.NewGuid(), profile) { HasRefreshToken = true });
                break;
            case "stored":
            case "stored-loopback":
                Set("_storedOAuthProfile", profile);
                type.GetMethod("EnsureOAuthMethodListed", flags)?.Invoke(vm, null);
                if (type.GetMethod("FindAuthenticationMethodIndex", flags)?.Invoke(vm, [ImapAuthenticationMethod.OAuth2]) is int index)
                    vm.SelectedIncomingServerAuthenticationMethodIndex = index;
                break;
            default:
                Set("_oauthDiscovery", offered);
                break;
        }
        type.GetMethod("NotifySecureSignInLayoutChanged", flags)?.Invoke(vm, null);
        type.GetMethod("NotifyAuthenticationMethodLayoutChanged", flags)?.Invoke(vm, null);
    }

    /// <summary>
    /// Starts a loopback sign-in on 127.0.0.1:47123 against GitLab's public metadata (its endpoints share the issuer host) with a client id that does not
    /// exist: the Custom Tab shows the provider's error, and a request to the listener (adb shell am start -a
    /// android.intent.action.VIEW -d "http://127.0.0.1:47123/?state=probe&amp;error=access_denied") shows whether the
    /// backgrounded app still answers. The outcome is logged.
    /// </summary>
    private static async Task<string> ProbeLoopbackAsync(IServiceProvider services)
    {
        var tokens = services.GetRequiredService<IOAuthTokenService>();
        var profile = new AccountOAuthProfile
        {
            Issuer = "https://gitlab.com",
            ClientId = "wino-loopback-probe",
            Scopes = "openid",
            RedirectKind = OAuthRedirectKind.Loopback,
            LoopbackPort = LoopbackProbePort,
            TrustSource = OAuthTrustSource.Discovered
        };
        _ = Task.Run(async () =>
        {
            var started = DateTime.UtcNow;
            try
            {
                await tokens.SignInAsync(profile, null);
                Serilog.Log.Information("Loopback probe: signed in after {Seconds:F0} s.", (DateTime.UtcNow - started).TotalSeconds);
            }
            catch (Exception exception)
            {
                Serilog.Log.Information("Loopback probe: ended after {Seconds:F0} s with {Type}: {Message} ({Inner})",
                    (DateTime.UtcNow - started).TotalSeconds, exception.GetType().Name, exception.Message, exception.InnerException?.Message);
            }
        });
        return "listening on 127.0.0.1:" + LoopbackProbePort;
    }
}
#endif
