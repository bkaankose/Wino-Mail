using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Wino.Mail.AI.Abstractions;
using Wino.Services;

namespace Wino.Intelligence.ConsoleApp.Hosting;

internal sealed record ConsolePaths(string ApplicationDataFolder, string PublisherFolder, string TempFolder);

internal enum ApiTarget
{
    Local,
    Production,
}

/// <summary>
/// Builds the same service graph the app builds (RegisterCoreServices + RegisterSharedServices),
/// with console stand-ins for the WinUI-only services, and starts it in the app's order.
/// </summary>
internal static class ConsoleServices
{
    public const string LocalApiUrl = "https://localhost:7204/";
    public const string ProductionApiUrl = "https://api.winomail.app/";

    public static Uri GetApiUri(ApiTarget target)
        => new(target == ApiTarget.Production ? ProductionApiUrl : LocalApiUrl);

    public static ServiceProvider Create(ConsolePaths paths, ApiTarget apiTarget)
    {
        var nativeAppService = new ConsoleNativeAppService(paths.ApplicationDataFolder);
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.RegisterCoreServices();
        services.RegisterSharedServices();

        // Registered by the WinUI app on top of the shared services.
        services.AddSingleton<IConfigurationService, ConsoleConfigurationService>();
        services.AddSingleton(ConsolePreferencesProxy.Create());
        services.AddSingleton<INativeAppService>(nativeAppService);
        services.AddSingleton<IAppMetadataService>(nativeAppService);
        services.AddSingleton<INotificationBuilder, ConsoleNotificationBuilder>();
        services.AddSingleton<IKeyPressService, ConsoleKeyPressService>();
        services.AddSingleton(ConsoleDialogProxy.Create());
        services.AddSingleton(ConsoleDefaultProxy<IStatePersistanceService>.Create());
        services.AddSingleton<IStoreManagementService, ConsoleStoreManagementService>();
        services.AddSingleton<IUserPresenceStateProvider, ConsoleUserPresenceStateProvider>();
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();

        // The shared registration builds its own HttpClient against the compiled-in URL. The
        // console picks the target at startup, so the client is rebuilt around that address.
        services.AddSingleton<IWinoAccountApiClient>(provider =>
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.GZip | DecompressionMethods.Deflate,
                ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                    (apiTarget == ApiTarget.Local && request.RequestUri?.IsLoopback == true) ||
                    errors == System.Net.Security.SslPolicyErrors.None,
            };

            return new WinoAccountApiClient(
                provider.GetRequiredService<IDatabaseService>(),
                new HttpClient(handler) { BaseAddress = GetApiUri(apiTarget) },
                provider.GetRequiredService<IContentEnvelopeEncryptor>(),
                provider.GetRequiredService<ITranslationService>(),
                sessionService: provider.GetRequiredService<IWinoAccountSessionService>());
        });

        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = false,
        });

        var configuration = provider.GetRequiredService<IApplicationConfiguration>();
        configuration.ApplicationDataFolderPath = paths.ApplicationDataFolder;
        configuration.PublisherSharedFolderPath = paths.PublisherFolder;
        configuration.ApplicationTempFolderPath = paths.TempFolder;
        return provider;
    }

    /// <summary>
    /// Mirrors the activation order in App.EnsureCoreActivationInfrastructureAsync and
    /// WinoApplication. The entitlement refresh is awaited here, where the app fires and forgets
    /// it, because every intelligence gate reads the refreshed state.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services)
    {
        await ConsoleOutput.TimedAsync("Main database", () => services.GetRequiredService<IDatabaseService>().InitializeAsync());
        await ConsoleOutput.TimedAsync("Intelligence database", () => services.GetRequiredService<IMailIntelligenceStore>().InitializeAsync());
        await ConsoleOutput.TimedAsync("Translations", () => services.GetRequiredService<ITranslationService>().InitializeAsync());
        await ConsoleOutput.TimedAsync("Synchronization manager", () => services.GetRequiredService<SynchronizationManagerInitializer>().InitializeAsync());

        var entitlement = services.GetRequiredService<IWinoIntelligenceEntitlementService>();
        await entitlement.GetAsync();
        await ConsoleOutput.TimedAsync("Entitlement refresh", () => entitlement.RefreshAsync());

        await services.GetRequiredService<IMailIntelligenceCoordinator>().InitializeAsync();
        await ConsoleOutput.TimedAsync("Result key lifecycle", () => services.GetRequiredService<IntelligenceResultKeyLifecycle>().InitializeAsync());
    }

    /// <summary>
    /// One anonymous request, so an API that is down or behind a maintenance page is reported as
    /// such instead of surfacing later as a JSON parse error from the client.
    /// </summary>
    public static async Task ProbeApiAsync(ApiTarget apiTarget)
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                (apiTarget == ApiTarget.Local && request.RequestUri?.IsLoopback == true) ||
                errors == System.Net.Security.SslPolicyErrors.None,
        };
        using var client = new HttpClient(handler) { BaseAddress = GetApiUri(apiTarget), Timeout = TimeSpan.FromSeconds(10) };

        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync("api/v2/ai/intelligence/transport-key");
            var status = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} in {ConsoleOutput.Elapsed(clock.Elapsed)}";

            // Without a token the route answers 401, which proves the API itself is serving.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.OK)
                ConsoleOutput.KeyValue("API reachable", status);
            else
                ConsoleOutput.Warning($"  API answered {status}. Calls to it will fail until it is serving again.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            ConsoleOutput.Warning($"  API unreachable: {exception.Message}. Mail synchronization still works; intelligence calls will fail.");
        }
    }

    /// <summary>IMAP synchronizers keep idle clients open, so each one is destroyed before exit.</summary>
    public static async Task ShutdownAsync(IServiceProvider services)
    {
        var manager = services.GetRequiredService<ISynchronizationManager>();
        foreach (var synchronizer in manager.GetAllSynchronizers().ToArray())
        {
            try
            {
                await manager.DestroySynchronizerAsync(synchronizer.Account.Id).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception exception)
            {
                ConsoleOutput.Muted($"Stopping the synchronizer for {synchronizer.Account.Address} failed: {exception.Message}");
            }
        }
    }
}
