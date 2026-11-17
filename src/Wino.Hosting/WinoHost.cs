using System.Reflection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wino.Authentication;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Activation;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Messaging.Client.Shell;
using Wino.Services;
using Wino.Services.Platform;
using Wino.Services.Security;

namespace Wino.Hosting;

/// <summary>
/// The shared backend, up to the ViewModels, composed and started without an app: real services, real network and
/// a real database below <see cref="WinoHostOptions.DataRoot"/>. Requests the backend makes of a screen (dialogs,
/// notifications, navigation) are answered as dismissed and recorded in <see cref="Interactions"/>.
/// One host per process: the backend still uses process-wide statics.
/// </summary>
public sealed partial class WinoHost : IAsyncDisposable
{
    /// <summary>Binds every protected envelope of a host; never change it, or existing data roots stop reading.</summary>
    private const string ApplicationIdentity = "com.winomail.host";
    private const string ProtectionKeyFileName = "protection.key";

    private readonly ServiceProvider _services;
    private readonly HostDispatcher _dispatcher;
    private readonly HostInteractionLog _log;
    private readonly HostLifecycle _lifecycle;
    private int _disposed;

    private WinoHost(ServiceProvider services, HostDispatcher dispatcher, HostInteractionLog log, string dataRoot)
    {
        _services = services;
        _dispatcher = dispatcher;
        _log = log;
        _lifecycle = new HostLifecycle(services, log);
        DataRoot = dataRoot;
    }

    public string DataRoot { get; }

    public IServiceProvider Services => _services;

    /// <summary>The host's UI thread. ViewModels that expect a UI thread run their work here.</summary>
    public HostDispatcher Dispatcher => _dispatcher;

    /// <summary>Everything the backend asked of a screen, in order, with the host's answer.</summary>
    public IReadOnlyList<HostInteraction> Interactions => _log.Items;

    /// <summary>The first content an app would show after startup.</summary>
    public ApplicationStartupTarget StartupTarget { get; private set; }

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    public static async Task<WinoHost> StartAsync(Action<WinoHostOptions> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new WinoHostOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.DataRoot))
            throw new ArgumentException("WinoHostOptions.DataRoot is required.", nameof(configure));

        var layout = HostLayout.Prepare(Path.GetFullPath(options.DataRoot));
        var dispatcher = new HostDispatcher();
        var log = new HostInteractionLog();
        ServiceProvider? services = null;

        try
        {
            services = Compose(layout, dispatcher, log);
            var host = new WinoHost(services, dispatcher, log, layout.Root);
            await host.StartCoreAsync(cancellationToken).ConfigureAwait(false);
            return host;
        }
        catch
        {
            if (services != null)
                await services.DisposeAsync().ConfigureAwait(false);

            dispatcher.Dispose();
            throw;
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Get<IMessenger>().Register<WinoHost, InfoBarMessageRequested>(this, static (host, message)
            => host._log.Record(HostInteractionKind.InfoBar, message.Severity.ToString(), $"{message.Title}: {message.Message}"));

        StartupTarget = await Get<ApplicationLifecycleCoordinator>().StartAsync(_lifecycle).ConfigureAwait(false);
    }

    private static ServiceProvider Compose(HostLayout layout, HostDispatcher dispatcher, HostInteractionLog log)
    {
        var protector = new AesGcmEnvelopeSecretProtector(new FileProtectionKeyProvider(layout.ProtectionKeyPath, ApplicationIdentity));
        if (layout.IsNewInstallation) protector.ProvisionNewInstallationKey(true);
        var secureItems = new ProtectedFileSecureItemStore(layout.SecretsRoot, protector);

        var services = new ServiceCollection();
        services.RegisterSharedServices();
        // Each host gets its own messenger, so recipients and messages never cross hosts in one process.
        services.Replace(ServiceDescriptor.Singleton<IMessenger>(new WeakReferenceMessenger()));
        services.RegisterCoreServices();
        AddHostPlatformServices(services, layout, dispatcher, log, protector, secureItems);
        services.RegisterWinoViewModels();
        AddHostHead(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>The contracts a platform supplies, in the order and with the lifetimes of the Android registration.</summary>
    private static void AddHostPlatformServices(IServiceCollection services, HostLayout layout, HostDispatcher dispatcher, HostInteractionLog log,
        AesGcmEnvelopeSecretProtector protector, ProtectedFileSecureItemStore secureItems)
    {
        services.AddSingleton(log);
        services.AddSingleton(dispatcher);
        services.AddSingleton<IDispatcher>(dispatcher);
        services.AddPortablePlatformServices(new HostApplicationConfiguration
        {
            ApplicationDataFolderPath = layout.ApplicationDataRoot,
            PublisherSharedFolderPath = Path.Combine(layout.ApplicationDataRoot, ApplicationConfiguration.SharedFolderName),
            ApplicationTempFolderPath = layout.TemporaryRoot
        }, secureItems, protector, CreateMetadata());

        services.AddSingleton<IApplicationResourceResolver>(provider => new FileSystemApplicationResourceResolver(
            AppContext.BaseDirectory, provider.GetRequiredService<IApplicationConfiguration>()));
        services.AddSingleton<IGoogleTokenStore, SecureItemGoogleTokenStore>();
        services.AddSingleton<IOAuthTokenStore, Wino.Authentication.OAuth.SecureItemOAuthTokenStore>();
        services.AddSingleton<IAuthenticatorConfig, MailAuthenticatorConfiguration>();
        services.AddSingleton<IAttachmentPlatformService, LauncherAttachmentPlatformService>();
        services.AddSingleton<IStartupIntegrationService, UnavailableStartupIntegrationService>();
        services.AddSingleton<ISmimeCertificateService, UnavailableSmimeCertificateService>();

        services.AddSingleton<IExternalLauncher, HostExternalLauncher>();
        services.AddSingleton<IClipboardService, HostClipboardService>();
        services.AddSingleton<IReaderRuntimeService, HostReaderRuntimeService>();
        services.AddSingleton<IShortcutPlatformService, HostShortcutPlatformService>();
        services.AddSingleton<IUserPresenceStateProvider, HostUserPresenceStateProvider>();
        services.AddSingleton<ITaskCompletionSound, HostTaskCompletionSound>();
        services.AddSingleton<IMicrosoftStoreService, HostStoreService>();
        services.AddSingleton<IUnderlyingThemeService, HostUnderlyingThemeService>();
        services.AddSingleton<IMicrophoneService, HostMicrophoneService>();
        services.Replace(ServiceDescriptor.Singleton<INotificationBuilder, HostNotificationBuilder>());

        // Sign-in surfaces report that interactive sign-in is unavailable; stored passwords and tokens still work.
        services.AddSingleton<IOutlookAuthenticationHost, HostOutlookAuthenticationHost>();
        services.Replace(ServiceDescriptor.Transient<IGoogleAuthorizationCodeReceiver, HostGoogleAuthorizationCodeReceiver>());
        services.AddSingleton<IOAuthBrowserBroker, HostOAuthBrowserBroker>();
        services.AddSingleton<IEmbeddedBrowserAuthBroker, HostEmbeddedBrowserAuthBroker>();
        services.AddSingleton<IWinoAccountBrowserAuthBroker, HostWinoAccountBrowserAuthBroker>();
        services.AddSingleton<IWinoAccountExternalSignInService>(provider => new WinoAccountExternalSignInService(
            provider.GetRequiredService<IWinoAccountApiClient>(), provider.GetRequiredService<IWinoAccountProfileService>(),
            apple: null, google: null, provider.GetRequiredService<IWinoAccountBrowserAuthBroker>()));

        // No relay device info, push token or attestation is registered, so live notifications report an unsupported
        // platform and never contact the relay. Notifications are on so the backend builds them and the host records them.
        services.AddSingleton<IPlatformCapabilities>(new PlatformCapabilities(Notifications: true, OAuthLoopbackRedirect: false));
    }

    private static void AddHostHead(IServiceCollection services)
    {
        services.AddSingleton<INavigationService, HostNavigationService>();
        services.AddSingleton<HostDialogService>();
        services.AddSingleton<IMailDialogService>(provider => provider.GetRequiredService<HostDialogService>());
        services.AddSingleton<IDialogServiceBase>(provider => provider.GetRequiredService<HostDialogService>());
        services.AddSingleton<INewThemeService, HostThemeService>();
        services.AddSingleton<IApplicationShellHost, HostApplicationShellHost>();
        services.AddSingleton<INotificationResponseNavigator, NotificationResponseNavigator>();
        services.AddSingleton<ApplicationLifecycleCoordinator>();
    }

    private static HostAppMetadataService CreateMetadata()
    {
        var assembly = typeof(WinoHost).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "0.0.0";
#if DEBUG
        const bool isDebug = true;
#else
        const bool isDebug = false;
#endif
        return new HostAppMetadataService(version, "Wino.Hosting", isDebug, null, "Host");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await Get<ApplicationLifecycleCoordinator>().StopAsync(_lifecycle).ConfigureAwait(false);
        }
        finally
        {
            Get<IMessenger>().UnregisterAll(this);
            await _services.DisposeAsync().ConfigureAwait(false);

            // sqlite-net pools connections per path for the whole process; closing the pool releases the database files.
            SQLite.SQLiteAsyncConnection.ResetPool();
            Serilog.Log.CloseAndFlush();
            _dispatcher.Dispose();
        }
    }

    /// <summary>The head side of the shared startup order: the registered lifecycle hooks, and no window to show.</summary>
    private sealed class HostLifecycle(IServiceProvider services, HostInteractionLog log) : IApplicationLifecycleHost
    {
        public async Task ServicesReadyAsync()
        {
            foreach (var hook in services.GetServices<IApplicationLifecycleHook>())
                await hook.ServicesReadyAsync();
        }

        public Task ShowStartupAsync(ApplicationStartupTarget target)
        {
            log.Record(HostInteractionKind.Startup, nameof(ShowStartupAsync), target.ToString(), "no window");
            return Task.CompletedTask;
        }

        public async Task StoppingAsync(bool runtimeStarted)
        {
            foreach (var hook in services.GetServices<IApplicationLifecycleHook>().Reverse())
                await hook.StoppingAsync(runtimeStarted);
        }

        public Task StopNavigationAsync() => Task.CompletedTask;
    }

    /// <summary>The folders below the data root. Everything the host writes stays inside it.</summary>
    private sealed record HostLayout(string Root, bool IsNewInstallation)
    {
        public string ApplicationDataRoot => Path.Combine(Root, "LocalState");
        public string TemporaryRoot => Path.Combine(Root, "Temp");
        public string SecretsRoot => Path.Combine(Root, "Secrets");
        public string ProtectionKeyPath => Path.Combine(Root, ProtectionKeyFileName);

        public static HostLayout Prepare(string root)
        {
            var isEmpty = !Directory.Exists(root) || !Directory.EnumerateFileSystemEntries(root).Any();
            if (!isEmpty && !File.Exists(Path.Combine(root, ProtectionKeyFileName)))
                throw new InvalidOperationException($"The data root '{root}' is not empty and was not created by a host. Use an empty folder.");

            var layout = new HostLayout(root, isEmpty);
            Directory.CreateDirectory(layout.ApplicationDataRoot);
            Directory.CreateDirectory(layout.TemporaryRoot);
            Directory.CreateDirectory(layout.SecretsRoot);
            return layout;
        }
    }
}
