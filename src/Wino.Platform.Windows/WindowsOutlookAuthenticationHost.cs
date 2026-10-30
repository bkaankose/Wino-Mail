using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;
using Wino.Authentication;
using Wino.Core.Domain;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Platform.Windows;

/// <summary>Windows WAM presentation and the existing MSAL Extensions cache, shared by every Outlook client id.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsOutlookAuthenticationHost : IOutlookAuthenticationHost, IDisposable
{
    private const string Authority = "https://login.microsoftonline.com/common";
    private readonly IApplicationConfiguration _configuration;
    private readonly IAuthenticatorConfig _authenticatorConfig;
    private readonly Func<nint> _parentLookup;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly ConcurrentDictionary<string, IPublicClientApplication> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _registeredClientIds = new(StringComparer.OrdinalIgnoreCase);
    private MsalCacheHelper _cache;

    public WindowsOutlookAuthenticationHost(IApplicationConfiguration configuration,
        IAuthenticatorConfig authenticatorConfig, Func<nint> parentLookup = null)
    {
        _configuration = configuration;
        _authenticatorConfig = authenticatorConfig;
        _parentLookup = parentLookup;
    }

    public bool CanAuthenticateInteractively => TryGetParent() != nint.Zero;

    public string LegacyClientId => OutlookClientIds.Legacy;

    public IPublicClientApplication Client => GetClient(OutlookClientIds.Current);

    public Task EnsureTokenCacheAttachedAsync(CancellationToken cancellationToken = default)
        => EnsureTokenCacheAttachedAsync(OutlookClientIds.Current, cancellationToken);

    public Task<AuthenticationResult> AcquireTokenInteractiveAsync(IEnumerable<string> scopes, IAccount account,
        string loginHint, CancellationToken cancellationToken = default)
        => AcquireTokenInteractiveAsync(OutlookClientIds.Current, scopes, account, loginHint, cancellationToken);

    public Task RemoveLocalAccountAsync(string authenticationAddress, CancellationToken cancellationToken = default)
        => RemoveLocalAccountAsync(OutlookClientIds.Current, authenticationAddress, cancellationToken);

    public IPublicClientApplication GetClient(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return _clients.GetOrAdd(clientId, BuildClient);
    }

    private IPublicClientApplication BuildClient(string clientId)
    {
        var options = new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
        {
            Title = _authenticatorConfig.ApplicationDisplayName,
            ListOperatingSystemAccounts = true,
        };
        var windowsOptions = new WindowsBrokerOptions
        {
            HeaderText = Translator.OutlookAuthentication_WamHeaderText,
            ListWindowsWorkAndSchoolAccounts = true,
        };
        var builder = PublicClientApplicationBuilder.Create(clientId)
            .WithBroker(options)
            .WithDefaultRedirectUri();

        if (_parentLookup is not null)
            builder = builder.WithParentActivityOrWindow(new Func<nint>(GetRequiredParent));

#pragma warning disable CS0618
        builder = builder.WithWindowsBrokerOptions(windowsOptions);
#pragma warning restore CS0618
        return builder.WithAuthority(Authority).Build();
    }

    public async Task EnsureTokenCacheAttachedAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var client = GetClient(clientId);

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is null)
            {
                var path = AuthenticationTokenStorePaths.GetOutlookTokenCachePath(_configuration);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _cache = await CreateCacheAsync(path).WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            // Both clients share OutlookCache.bin; MSAL keys every cache entry by client id.
            if (_registeredClientIds.Add(clientId))
                _cache.RegisterCache(client.UserTokenCache);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<AuthenticationResult> AcquireTokenInteractiveAsync(string clientId, IEnumerable<string> scopes,
        IAccount account, string loginHint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = GetRequiredParent();
        await EnsureTokenCacheAttachedAsync(clientId, cancellationToken).ConfigureAwait(false);
        var builder = GetClient(clientId).AcquireTokenInteractive(scopes);

        if (account is not null) builder = builder.WithAccount(account);
        else if (!string.IsNullOrWhiteSpace(loginHint)) builder = builder.WithLoginHint(loginHint);

        return await builder.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveLocalAccountAsync(string clientId, string authenticationAddress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var localClient = PublicClientApplicationBuilder.Create(clientId)
            .WithDefaultRedirectUri().WithAuthority(Authority).Build();
        var path = AuthenticationTokenStorePaths.GetOutlookTokenCachePath(_configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var cache = await CreateCacheAsync(path).WaitAsync(cancellationToken).ConfigureAwait(false);
        cache.RegisterCache(localClient.UserTokenCache);

        try
        {
            var account = (await localClient.GetAccountsAsync().WaitAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(value => string.Equals(value.Username?.Trim(), authenticationAddress?.Trim(), StringComparison.OrdinalIgnoreCase));

            if (account is not null)
                await localClient.RemoveAsync(account).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            cache.UnregisterCache(localClient.UserTokenCache);
        }
    }

    public void Dispose()
    {
        if (_cache is not null)
        {
            foreach (var clientId in _registeredClientIds)
            {
                if (_clients.TryGetValue(clientId, out var client))
                    _cache.UnregisterCache(client.UserTokenCache);
            }
        }

        _cacheLock.Dispose();
    }

    private static Task<MsalCacheHelper> CreateCacheAsync(string path)
        => MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder(Path.GetFileName(path), Path.GetDirectoryName(path)!).Build());

    private nint GetRequiredParent()
    {
        var parent = TryGetParent();
        if (parent == nint.Zero)
            throw new InvalidOperationException("A live application window is required for interactive Windows authentication.");

        return parent;
    }

    private nint TryGetParent()
    {
        if (_parentLookup is null) return nint.Zero;

        nint parent;
        try { parent = _parentLookup(); }
        catch (ObjectDisposedException) { return nint.Zero; }
        catch (InvalidOperationException) { return nint.Zero; }

        if (parent == nint.Zero || !IsWindow(parent)) return nint.Zero;

        _ = GetWindowThreadProcessId(parent, out var processId);
        return processId == (uint)Environment.ProcessId ? parent : nint.Zero;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
}
