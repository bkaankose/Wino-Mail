#nullable enable
using System;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.PushRelay.Socket.Transport;

namespace Wino.PushRelay.Socket;

public static class RelaySocketServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IWinoRelaySocketClient"/> over the shared relay services. The head registers an
    /// <see cref="IPushRelaySocketKeyStore"/> for its platform first; nothing starts the socket here.
    /// </summary>
    public static IServiceCollection AddWinoRelaySocketClient(this IServiceCollection services, Action<WinoRelaySocketOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new WinoRelaySocketOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IRelayHubConnectionFactory, SignalRRelayHubConnectionFactory>();
        services.AddSingleton<IWinoRelaySocketClient>(provider => new WinoRelaySocketClient(
            provider.GetRequiredService<IPushRelayTokenProvider>(),
            provider.GetRequiredService<IPushRelayClient>(),
            provider.GetRequiredService<IPushRelayDeviceIdentityStore>(),
            provider.GetRequiredService<IRelayHubConnectionFactory>(),
            provider.GetService<IPushRelaySocketKeyStore>(),
            provider.GetRequiredService<WinoRelaySocketOptions>()));
        return services;
    }
}
