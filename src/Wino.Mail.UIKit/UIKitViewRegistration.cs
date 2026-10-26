using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Views;
using Wino.Mail.UIKit.Views.Onboarding;
using Wino.Mail.ViewModels.Activation;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.iOS.Services;
using Wino.Services.Themes;

namespace Wino.Mail.UIKit;

/// <summary>
/// Registers the idiom-neutral UIKit layer. The head calls it after the shared, core and iOS platform registrations
/// and <c>RegisterWinoViewModels()</c>; the idiom registration (AddPhoneShell or AddPadShell) then supplies the
/// <see cref="IUIKitPageRegistry"/> and <see cref="IUIKitShellHost"/> these services depend on.
/// </summary>
public static partial class UIKitViewRegistration
{
    public static IServiceCollection RegisterUIKitViews(this IServiceCollection services)
    {
        // Onboarding pages (the same on iPhone and iPad).
        services.AddTransient<WelcomePageV2ViewController>();
        services.AddTransient<ProviderSelectionPageViewController>();
        services.AddTransient<AccountSetupProgressPageViewController>();
        services.AddTransient<SpecialImapCredentialsPageViewController>();
        services.AddTransient<ImapCalDavSettingsPageViewController>();

        // Feature pages in their own partial files: Mail (reader, compose, idle, What's New, briefing) and Settings.
        RegisterMailViews(services);
        RegisterSettingsViews(services);
        RegisterContactsViews(services);
        RegisterTasksViews(services);
        RegisterCalendarViews(services);
        return services;
    }

    static partial void RegisterCalendarViews(IServiceCollection services);

    /// <summary>Navigation, activation, lifecycle, dialogs, theme and presenters over the idiom's shell host.</summary>
    public static IServiceCollection RegisterUIKitServices(this IServiceCollection services, IDispatcher dispatcher)
    {
        services.AddSingleton(dispatcher);
        services.AddSingleton<UIKitNavigationService>(provider => new UIKitNavigationService(provider, dispatcher,
            provider.GetRequiredService<IUIKitPageRegistry>(), provider.GetRequiredService<IUIKitShellHost>()));
        services.AddSingleton<INavigationService>(provider => provider.GetRequiredService<UIKitNavigationService>());

        // Activation, notification responses and the startup order (shared rules, UIKit hosts).
        services.AddSingleton<IApplicationShellHost, UIKitApplicationShellHost>();
        services.AddSingleton<ActivationRouter>();
        services.AddSingleton<UIKitActivationCoordinator>();
        services.AddSingleton<INotificationResponseNavigator, NotificationResponseNavigator>();
        services.AddSingleton<ApplicationLifecycleCoordinator>();
        services.AddSingleton<UIKitApplicationLifecycleHost>();

        services.AddSingleton<IMailDialogService, UIKitDialogService>();
        services.AddSingleton<IDialogServiceBase>(provider => provider.GetRequiredService<IMailDialogService>());

        // Wino themes on top of the system appearance; custom themes use the Windows file layout.
        services.AddSingleton(provider => new CustomThemeFileStore(provider.GetRequiredService<IApplicationConfiguration>()));
        services.AddSingleton<UIKitThemeService>();
        services.AddSingleton<INewThemeService>(provider => provider.GetRequiredService<UIKitThemeService>());
        services.TryAddSingleton<IUnderlyingThemeService, UIKitUnderlyingThemeService>();

        services.AddSingleton<UIKitSenderPictureProvider>();
        services.AddSingleton<UIKitAuthenticationPresenter>();
        services.AddSingleton<IIOSFilePresenter, UIKitFilePresenter>();
        return services;
    }
}
