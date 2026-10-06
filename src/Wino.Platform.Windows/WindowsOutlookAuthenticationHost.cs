using System;
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
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Platform.Windows;

/// <summary>Windows WAM presentation and the existing MSAL Extensions cache.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsOutlookAuthenticationHost : IOutlookAuthenticationHost, IDisposable
{
    private const string Authority = "https://login.microsoftonline.com/common";
    private readonly IApplicationConfiguration _configuration;
    private readonly IAuthenticatorConfig _authenticatorConfig;
    private readonly Func<nint> _parentLookup;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private MsalCacheHelper _cache;

    public WindowsOutlookAuthenticationHost(IApplicationConfiguration configuration,
        IAuthenticatorConfig authenticatorConfig, Func<nint> parentLookup = null)
    {
        _configuration = configuration;
        _authenticatorConfig = authenticatorConfig;
        _parentLookup = parentLookup;

        var options = new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
        {
            Title = authenticatorConfig.ApplicationDisplayName,
            ListOperatingSystemAccounts = true,
        };
        var windowsOptions = new WindowsBrokerOptions
        {
            HeaderText = Translator.OutlookAuthentication_WamHeaderText,
            ListWindowsWorkAndSchoolAccounts = true,
        };
        var builder = PublicClientApplicationBuilder.Create(authenticatorConfig.OutlookAuthenticatorClientId)
            .WithBroker(options)
            .WithDefaultRedirectUri();

        if (parentLookup is not null)
            builder = builder.WithParentActivityOrWindow(new Func<nint>(GetRequiredParent));

#pragma warning disable CS0618
        builder = builder.WithWindowsBrokerOptions(windowsOptions);
#pragma warning restore CS0618
        Client = builder.WithAuthority(Authority).Build();
    }

    public IPublicClientApplication Client { get; }
    public bool CanAuthenticateInteractively => TryGetParent() != nint.Zero;

    public async Task EnsureTokenCacheAttachedAsync(CancellationToken cancellationToken = default)
    {
        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cache is not null) return;

            var path = AuthenticationTokenStorePaths.GetOutlookTokenCachePath(_configuration);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var cache = await CreateCacheAsync(path).WaitAsync(cancellationToken).ConfigureAwait(false);
            cache.RegisterCache(Client.UserTokenCache);
            _cache = cache;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<AuthenticationResult> AcquireTokenInteractiveAsync(IEnumerable<string> scopes,
        IAccount account, string loginHint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = GetRequiredParent();
        await EnsureTokenCacheAttachedAsync(cancellationToken).ConfigureAwait(false);
        var builder = Client.AcquireTokenInteractive(scopes);

        if (account is not null) builder = builder.WithAccount(account);
        else if (!string.IsNullOrWhiteSpace(loginHint)) builder = builder.WithLoginHint(loginHint);

        return await builder.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveLocalAccountAsync(string authenticationAddress, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var localClient = PublicClientApplicationBuilder.Create(_authenticatorConfig.OutlookAuthenticatorClientId)
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
        _cache?.UnregisterCache(Client.UserTokenCache);
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
