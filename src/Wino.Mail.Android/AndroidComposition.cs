using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Serilog;
using Wino.Core;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Activation;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Android;
using Wino.Platform.Android.Notifications;
using Wino.Services;
using Wino.Services.Themes;

namespace Wino.Mail.Android;

/// <summary>
/// The composition root, in the iOS Composition order: platform bootstrap, shared and core services, the Android
/// platform, ViewModels, then the head's navigation, dialogs, theme and lifecycle. It depends on the application
/// context only, never on an activity, so work that runs without UI (background synchronization, notification
/// actions) can reuse the same container.
/// </summary>
internal static class AndroidComposition
{
    public static ServiceProvider Create(global::Android.App.Application application, AndroidActivityTracker activities)
    {
        // Paths and protection precede database or credential service construction.
        var bootstrap = AndroidPlatformBootstrap.Create(application);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(dispose: false);
        });
        services.RegisterSharedServices();
        services.RegisterCoreServices();
        services.AddAndroidPlatformServices(bootstrap, () => activities.Current);

        services.RegisterWinoViewModels();
        services.RegisterAndroidHead(activities);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Navigation, activation, lifecycle, dialogs, theme and the window size class.</summary>
    private static void RegisterAndroidHead(this IServiceCollection services, AndroidActivityTracker activities)
    {
        services.AddSingleton(activities);
        services.AddSingleton<AndroidWindowSizeClassService>();
        services.AddSingleton<IWindowSizeClassService>(provider => provider.GetRequiredService<AndroidWindowSizeClassService>());

        services.AddSingleton<AndroidPageRegistry>();
        services.AddSingleton<Views.Mail.AndroidSenderPictureProvider>();
        // The Daily briefing entry and sheet (shown only through the shared entitlement rule).
        services.AddSingleton<Views.Extras.AndroidDailyBriefingPresenter>();
        services.AddSingleton<AndroidNavigationService>(provider => new AndroidNavigationService(provider,
            provider.GetRequiredService<IDispatcher>(), provider.GetRequiredService<AndroidPageRegistry>()));
        services.AddSingleton<INavigationService>(provider => provider.GetRequiredService<AndroidNavigationService>());

        // Activation, notification responses and the startup order (shared rules, Android hosts).
        services.AddSingleton<IApplicationShellHost, AndroidApplicationShellHost>();
        services.AddSingleton<ActivationRouter>();
        services.AddSingleton<INotificationResponseNavigator, NotificationResponseNavigator>();
        services.AddSingleton<ApplicationLifecycleCoordinator>();
        services.AddSingleton<AndroidApplicationLifecycleHost>();

        services.AddSingleton<IMailDialogService>(provider => new AndroidDialogService(
            provider.GetRequiredService<IDispatcher>(), activities, provider));
        services.AddSingleton<IDialogServiceBase>(provider => provider.GetRequiredService<IMailDialogService>());
        services.AddSingleton(provider => new AndroidDocumentPicker(
            provider.GetRequiredService<Wino.Platform.Android.Authentication.AndroidActivityResults>(), global::Android.App.Application.Context));

        // Wino themes; custom themes use the Windows file layout.
        services.AddSingleton(provider => new CustomThemeFileStore(provider.GetRequiredService<IApplicationConfiguration>()));
        services.AddSingleton<AndroidThemeService>();
        services.AddSingleton<INewThemeService>(provider => provider.GetRequiredService<AndroidThemeService>());
        services.TryAddSingleton<IUnderlyingThemeService, AndroidUnderlyingThemeService>();

        // Notifications, reminder wake-ups and the queued-changes flush; taps, actions, alarms and work arrive at
        // these head components (Background/).
        services.AddAndroidNotificationServices(
            new AndroidNotificationComponents(typeof(MainActivity), typeof(Background.NotificationActionReceiver)),
            typeof(Background.ReminderAlarmReceiver), typeof(Background.PendingChangesWorker));
        services.AddSingleton<AndroidNotificationPermission>();

        // Head lifecycle hooks (IApplicationLifecycleHook) run in registration order once the runtime started.
        services.AddSingleton<IApplicationLifecycleHook, Background.NotificationsLifecycleHook>();
    }
}
