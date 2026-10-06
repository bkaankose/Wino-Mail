#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Wino.Authentication;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.MacOS;

/// <summary>System-browser MSAL host with MSAL Extensions' native macOS Keychain cache.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacOSOutlookAuthenticationHost : IOutlookAuthenticationHost, IDisposable, IAsyncDisposable
{
    private readonly IApplicationConfiguration _configuration;
    private readonly Func<bool> _canPresent;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly object _lifetimeGate = new();
    private bool _disposed;
    private Task? _disposeTask;
    private MsalCacheHelper? _cache;

    public MacOSOutlookAuthenticationHost(IApplicationConfiguration configuration,
        IAuthenticatorConfig authenticatorConfig, Func<bool> canPresent)
    {
        _configuration = configuration;
        _canPresent = canPresent ?? throw new ArgumentNullException(nameof(canPresent));
        Client = PublicClientApplicationBuilder.Create(authenticatorConfig.OutlookAuthenticatorClientId)
            .WithDefaultRedirectUri()
            .WithAuthority("https://login.microsoftonline.com/common")
            .Build();
    }

    public IPublicClientApplication Client { get; }
    public bool CanAuthenticateInteractively
    {
        get
        {
            lock (_lifetimeGate)
                return !_disposed && OperatingSystem.IsMacOS() && _canPresent();
        }
    }

    public async Task EnsureTokenCacheAttachedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The macOS authentication cache requires Keychain.");
        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_cache is not null) return;
            // MSAL Extensions stores cache contents in Keychain on macOS. The filename/directory
            // parameters remain required by its API; no WithUnprotectedFile fallback is enabled.
            var properties = new StorageCreationPropertiesBuilder("msal.cache", _configuration.ApplicationDataFolderPath)
                .WithMacKeyChain($"{MacOSApplicationIdentity.Value}.v1.msal", "outlook-user-cache")
                .Build();
            Directory.CreateDirectory(_configuration.ApplicationDataFolderPath);
            // Creation has no cancellation API. Own its completion rather than abandoning a
            // helper while shutdown or cancellation proceeds in another thread.
            var cache = await MsalCacheHelper.CreateAsync(properties).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            cache.VerifyPersistence();
            lock (_lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cache.RegisterCache(Client.UserTokenCache);
                _cache = cache;
            }
        }
        finally { _cacheLock.Release(); }
    }

    public async Task<AuthenticationResult> AcquireTokenInteractiveAsync(IEnumerable<string> scopes,
        IAccount? account, string? loginHint, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanAuthenticateInteractively) throw new InvalidOperationException("Interactive macOS authentication requires an active application window.");
        await EnsureTokenCacheAttachedAsync(cancellationToken).ConfigureAwait(false);
        var request = Client.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false);
        if (account is not null) request = request.WithAccount(account);
        else if (!string.IsNullOrWhiteSpace(loginHint)) request = request.WithLoginHint(loginHint);
        return await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveLocalAccountAsync(string authenticationAddress, CancellationToken cancellationToken = default)
    {
        await EnsureTokenCacheAttachedAsync(cancellationToken).ConfigureAwait(false);
        var accounts = await Client.GetAccountsAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var account in accounts.Where(value => string.Equals(value.Username?.Trim(), authenticationAddress?.Trim(), StringComparison.OrdinalIgnoreCase)))
            await Client.RemoveAsync(account).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(BeginDispose());

    /// <summary>Initiates terminal shutdown. Await DisposeAsync to observe completed cache detachment.</summary>
    public void Dispose()
    {
        var cleanup = BeginDispose();
        if (cleanup.IsCompleted) cleanup.GetAwaiter().GetResult();
        else _ = cleanup.ContinueWith(static task => { _ = task.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task BeginDispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposeTask is not null) return _disposeTask;
            _disposed = true;
            return _disposeTask = DetachCacheAsync();
        }
    }

    private async Task DetachCacheAsync()
    {
        await _cacheLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _cache?.UnregisterCache(Client.UserTokenCache);
            _cache = null;
        }
        finally { _cacheLock.Release(); }
        // Do not dispose this managed semaphore: an entrant that passed the initial lifetime
        // check can still be queued. It observes the terminal state after acquiring the gate
        // and can safely release it. No native wait handle is allocated by this host.
    }

    private void ThrowIfDisposed()
    {
        lock (_lifetimeGate) ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
