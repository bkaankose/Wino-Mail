using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Providers;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.Linux.Views;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Activation;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Linux;
using Wino.Providers.Dav.Providers;
using Wino.Providers.Exchange.Providers;
using Wino.Providers.Gmail.Providers;
using Wino.Providers.Imap.Providers;
using Wino.Providers.Outlook.Providers;
using Wino.Services;

namespace Wino.Mail.Linux;

/// <summary>
/// The composition root, in the order of the other heads: shared and core services, providers, the Linux platform,
/// ViewModels, then this head's navigation, dialogs, theme and lifecycle. Call it on the GTK main thread.
/// </summary>
internal static class LinuxComposition
{
    public static ServiceProvider Create(LinuxPlatformBootstrap bootstrap, Func<MainWindow?> window, LaunchOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: false);
        });
        services.RegisterSharedServices();
        services.RegisterCoreServices();
        services.AddGmailProvider().AddOutlookProvider().AddImapProvider().AddExchangeProvider().AddDavProvider();
        services.AddLinuxPlatformServices(bootstrap, () => window()?.Window);

        services.RegisterWinoViewModels();
        services.RegisterLinuxHead(window, options);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static void RegisterLinuxHead(this IServiceCollection services, Func<MainWindow?> window, LaunchOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(provider => new GtkNavigationService(provider, provider.GetRequiredService<IDispatcher>(), window));
        services.AddSingleton<INavigationService>(provider => provider.GetRequiredService<GtkNavigationService>());

        services.AddSingleton<IApplicationShellHost, GtkApplicationShellHost>();
        services.AddSingleton<ActivationRouter>();
        services.AddSingleton<INotificationResponseNavigator, NotificationResponseNavigator>();
        services.AddSingleton<ApplicationLifecycleCoordinator>();
        services.AddSingleton(provider => new GtkApplicationLifecycleHost(provider, provider.GetRequiredService<IDispatcher>(), window, options));

        services.AddSingleton(provider => new GtkDialogService(provider.GetRequiredService<IDispatcher>(), window));
        services.AddSingleton<IMailDialogService>(provider => provider.GetRequiredService<GtkDialogService>());
        services.AddSingleton<IDialogServiceBase>(provider => provider.GetRequiredService<GtkDialogService>());
        services.AddSingleton<INewThemeService, GtkThemeService>();
    }
}
