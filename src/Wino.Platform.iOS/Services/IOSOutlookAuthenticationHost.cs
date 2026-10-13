using Microsoft.Identity.Client;
using UIKit;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;
using Wino.Platform.Apple;

namespace Wino.Platform.iOS.Services;

/// <summary>
/// MSAL public client for iOS. MSAL keeps its token cache in the Keychain itself, in the
/// <c>com.microsoft.adalcache</c> access group (the app must list it in <c>keychain-access-groups</c>), so no
/// MsalCacheHelper is attached. Interactive sign-in uses the system web view (ASWebAuthenticationSession through
/// MSAL), presented from the head's current view controller.
/// </summary>
public sealed class IOSOutlookAuthenticationHost : IOutlookAuthenticationHost
{
    /// <summary>MSAL's shared Keychain access group on iOS.</summary>
    public const string KeychainSecurityGroup = "com.microsoft.adalcache";

    private readonly Func<UIViewController?> _presenter;

    public IOSOutlookAuthenticationHost(IAuthenticatorConfig authenticatorConfig, AppleApplicationIdentity identity, Func<UIViewController?> presenter)
    {
        ArgumentNullException.ThrowIfNull(authenticatorConfig);
        ArgumentNullException.ThrowIfNull(identity);
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        RedirectUri = GetRedirectUri(identity);
        Client = PublicClientApplicationBuilder.Create(authenticatorConfig.OutlookAuthenticatorClientId)
            .WithRedirectUri(RedirectUri)
            .WithAuthority("https://login.microsoftonline.com/common")
            .WithIosKeychainSecurityGroup(KeychainSecurityGroup)
            // MSAL's own diagnostics land in wino.log. The authenticator swallows MsalUiRequired and
            // MsalClient exceptions, so without this the log only ever shows the resulting attention state.
            .WithLogging(ForwardMsalLog, MsalLogLevel, enablePiiLogging: false, enableDefaultPlatformLogging: false)
            .Build();
    }

    /// <summary>The broker-style redirect URI MSAL requires on iOS: <c>msauth.&lt;bundle id&gt;://auth</c>.</summary>
    public static string GetRedirectUri(AppleApplicationIdentity identity) => $"msauth.{identity.Value}://auth";

    public string RedirectUri { get; }

#if DEBUG
    private const LogLevel MsalLogLevel = LogLevel.Verbose;
#else
    private const LogLevel MsalLogLevel = LogLevel.Info;
#endif

    private static readonly Serilog.ILogger MsalLogger = Serilog.Log.ForContext("SourceContext", "MSAL");

    private static void ForwardMsalLog(LogLevel level, string message, bool containsPii)
    {
        if (containsPii) return;
        switch (level)
        {
            case LogLevel.Error: MsalLogger.Error("{Message}", message); break;
            case LogLevel.Warning: MsalLogger.Warning("{Message}", message); break;
            case LogLevel.Info: MsalLogger.Debug("{Message}", message); break;
            default: MsalLogger.Verbose("{Message}", message); break;
        }
    }

    public IPublicClientApplication Client { get; }

    /// <summary>True while the head has a view controller in a window to present the sign-in sheet from.</summary>
    public bool CanAuthenticateInteractively => IOSMainThread.Read(() => CurrentPresenter() is not null);

    /// <summary>MSAL attaches its own Keychain cache on iOS; there is nothing to attach.</summary>
    public Task EnsureTokenCacheAttachedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<AuthenticationResult> AcquireTokenInteractiveAsync(IEnumerable<string> scopes,
        IAccount? account, string? loginHint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = IOSMainThread.Read(CurrentPresenter)
            ?? throw new InvalidOperationException("Interactive iOS authentication requires a visible view controller.");
        var request = Client.AcquireTokenInteractive(scopes)
            .WithUseEmbeddedWebView(false)
            .WithParentActivityOrWindow(parent);
        if (account is not null) request = request.WithAccount(account);
        else if (!string.IsNullOrWhiteSpace(loginHint)) request = request.WithLoginHint(loginHint);
        return await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveLocalAccountAsync(string authenticationAddress, CancellationToken cancellationToken = default)
    {
        var accounts = await Client.GetAccountsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var account in accounts.Where(value => string.Equals(value.Username?.Trim(), authenticationAddress?.Trim(), StringComparison.OrdinalIgnoreCase)))
            await Client.RemoveAsync(account).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The head's view controller, only while it is in a window (MSAL presents the session from it).</summary>
    private UIViewController? CurrentPresenter()
    {
        var controller = _presenter();
        return controller?.ViewIfLoaded?.Window is null ? null : controller;
    }
}
