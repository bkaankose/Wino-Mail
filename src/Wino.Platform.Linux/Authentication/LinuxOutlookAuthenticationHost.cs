using Microsoft.Identity.Client;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Linux.Authentication;

/// <summary>
/// MSAL host for Outlook on Linux: the Entra app of <see cref="IAuthenticatorConfig"/>, interactive sign-in in the
/// default browser with MSAL's <c>http://localhost</c> listener, the browser opened through <see cref="IExternalLauncher"/>
/// (portal-aware, unlike MSAL's own <c>xdg-open</c>), and the token cache as one secure item. <c>MsalCacheHelper</c> is
/// not used: its libsecret binding calls variadic functions and would add a second keyring schema.
/// </summary>
public sealed class LinuxOutlookAuthenticationHost : IOutlookAuthenticationHost, IDisposable
{
    /// <summary>Permanent: renaming the item signs every Outlook account out.</summary>
    public const string CacheScope = "msal-cache";

    /// <summary>Permanent, as <see cref="CacheScope"/>.</summary>
    public const string CacheAccount = "outlook-user-cache";

    private const string ProviderName = "Microsoft";

    private readonly ISecureItemStore _secureItems;
    private readonly IExternalLauncher _launcher;
    private readonly Func<IExternalBrowserAuthenticationPresenter?> _presenter;
    private readonly Func<bool> _canPresent;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly SemaphoreSlim _interactive = new(1, 1);
    private volatile bool _disposed;
    private bool _cacheAttached;

    public LinuxOutlookAuthenticationHost(IAuthenticatorConfig config, ISecureItemStore secureItems, IExternalLauncher launcher,
        Func<IExternalBrowserAuthenticationPresenter?> presenter, Func<bool> canPresent, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(config);
        _secureItems = secureItems ?? throw new ArgumentNullException(nameof(secureItems));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _canPresent = canPresent ?? throw new ArgumentNullException(nameof(canPresent));
        _timeout = timeout;
        // The Entra registration is for "localhost"; 127.0.0.1 would not match it. MSAL picks a free port.
        Client = PublicClientApplicationBuilder.Create(config.OutlookAuthenticatorClientId)
            .WithRedirectUri("http://localhost")
            .WithAuthority("https://login.microsoftonline.com/common")
            .WithWinoLogging()
            .Build();
    }

    public IPublicClientApplication Client { get; }

    public bool CanAuthenticateInteractively => !_disposed && _canPresent();

    public async Task EnsureTokenCacheAttachedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cacheAttached) return;
            // MSAL runs these under its cache lock, synchronously: file I/O and AES-GCM only, no dispatcher.
            Client.UserTokenCache.SetBeforeAccess(args =>
            {
                if (_disposed) return;
                byte[]? bytes = null;
                try { bytes = _secureItems.Read(CacheScope, CacheAccount); }
                catch (Exception exception) { Serilog.Log.Error("The Outlook token cache could not be read ({Type}); it is treated as empty.", exception.GetType().Name); }
                args.TokenCache.DeserializeMsalV3(bytes, shouldClearExistingCache: true);
            });
            Client.UserTokenCache.SetAfterAccess(args =>
            {
                if (_disposed || !args.HasStateChanged) return;
                try { _secureItems.Write(CacheScope, CacheAccount, args.TokenCache.SerializeMsalV3()); }
                catch (Exception exception)
                {
                    Serilog.Log.Error("The Outlook token cache could not be written ({Type}).", exception.GetType().Name);
                    throw;
                }
            });
            _cacheAttached = true;
        }
        finally { _cacheLock.Release(); }
    }

    public async Task<AuthenticationResult> AcquireTokenInteractiveAsync(IEnumerable<string> scopes, IAccount? account, string? loginHint,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanAuthenticateInteractively) throw new InvalidOperationException("Interactive Microsoft sign-in needs an open application window.");
        // MSAL would run two loopback listeners side by side; one sign-in at a time.
        if (!await _interactive.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Another Microsoft sign-in is in progress.");
        try
        {
            await EnsureTokenCacheAttachedAsync(cancellationToken).ConfigureAwait(false);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(_timeout);
            IExternalBrowserAuthenticationSession? session = null;

            async Task OpenBrowserAsync(Uri authorizationUri)
            {
                if (session is null && _presenter() is { } presenter)
                    session = await presenter.ShowAsync(new ExternalBrowserAuthenticationRequest(ProviderName, authorizationUri), () => TryCancel(limit)).ConfigureAwait(false);
                var launch = await _launcher.LaunchUriAsync(authorizationUri, limit.Token).ConfigureAwait(false);
                if (launch.IsSuccess) return;
                if (session is null) throw new InvalidOperationException(launch.ErrorMessage ?? "The sign-in page could not be opened in a browser.");
                await session.NotifyBrowserLaunchFailedAsync().ConfigureAwait(false);
            }

            var request = Client.AcquireTokenInteractive(scopes)
                .WithUseEmbeddedWebView(false)
                .WithSystemWebViewOptions(new SystemWebViewOptions { OpenBrowserAsync = OpenBrowserAsync });
            if (account is not null) request = request.WithAccount(account);
            else if (!string.IsNullOrWhiteSpace(loginHint)) request = request.WithLoginHint(loginHint);

            try
            {
                var result = await request.ExecuteAsync(limit.Token).ConfigureAwait(false);
                if (session is not null) await session.NotifyRedirectReceivedAsync().ConfigureAwait(false);
                return result;
            }
            finally
            {
                if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally { _interactive.Release(); }
    }

    public async Task RemoveLocalAccountAsync(string authenticationAddress, CancellationToken cancellationToken = default)
    {
        await EnsureTokenCacheAttachedAsync(cancellationToken).ConfigureAwait(false);
        var accounts = await Client.GetAccountsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var account in accounts.Where(value => string.Equals(value.Username?.Trim(), authenticationAddress?.Trim(), StringComparison.OrdinalIgnoreCase)))
            await Client.RemoveAsync(account).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>MSAL cannot detach cache callbacks; after this they do nothing.</summary>
    public void Dispose() => _disposed = true;

    private static void TryCancel(CancellationTokenSource source)
    {
        try { source.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
