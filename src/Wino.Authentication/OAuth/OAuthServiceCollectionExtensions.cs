#nullable enable
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;

namespace Wino.Authentication.OAuth;

public static class OAuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OAuth protocol client, token service and issuer discovery. Each head registers its own
    /// <see cref="IOAuthTokenStore"/> and, where custom-scheme sign-in exists, its <see cref="IOAuthBrowserBroker"/>.
    /// </summary>
    public static IServiceCollection AddWinoOAuth(this IServiceCollection services)
    {
        services.AddSingleton<IOAuthProtocolClient, OAuthProtocolClient>();

        // The browser pieces resolve on first sign-in, not when the singleton is built, so a head's UI services
        // are never constructed early by a background token refresh.
        services.AddSingleton<IOAuthTokenService>(provider => new OAuthTokenService(
            provider.GetRequiredService<IOAuthProtocolClient>(),
            provider.GetRequiredService<IOAuthTokenStore>(),
            () => provider.GetService<IOAuthBrowserBroker>(),
            () => provider.GetService<IExternalLauncher>(),
            () => provider.GetService<IExternalBrowserAuthenticationPresenter>(),
            AllowsLoopbackRedirect(provider)));

        services.AddTransient<IOAuthIssuerDiscoveryService>(provider => new OAuthIssuerDiscoveryService(
            provider.GetRequiredService<IKnownImapProviderCatalog>(),
            provider.GetRequiredService<IOAuthProtocolClient>(),
            AllowsLoopbackRedirect(provider)));

        return services;
    }

    private static bool AllowsLoopbackRedirect(System.IServiceProvider provider)
        => provider.GetService<IPlatformCapabilities>()?.OAuthLoopbackRedirect ?? true;
}
