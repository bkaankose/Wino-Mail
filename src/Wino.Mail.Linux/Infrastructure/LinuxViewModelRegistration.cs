using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Activation;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// The head's registrations: ViewModels, navigation, lifecycle, dialogs and theme, then one hook per area. Hook
/// <c>RegisterMail</c> lives in <c>LinuxViewModelRegistration.Mail.cs</c>; a hook without a file compiles to nothing.
/// Hooks run after the common registrations, so one may replace them. New hooks go at the end of both lists.
/// </summary>
internal static partial class LinuxViewModelRegistration
{
    public static IServiceCollection RegisterLinuxHead(this IServiceCollection services, Func<MainWindow?> window, LaunchOptions options)
    {
        services.RegisterWinoViewModels();

        services.AddSingleton(options);
        services.AddSingleton<WinoViewServices>();
        services.AddSingleton<LinuxPageRegistry>();
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

        RegisterShell(services);
        RegisterMail(services);
        RegisterCompose(services);
        RegisterOnboarding(services);
        RegisterAccounts(services);
        RegisterSettings(services);
        RegisterCalendar(services);
        RegisterContacts(services);
        RegisterTasks(services);
        RegisterIntelligence(services);
        RegisterDialogs(services);
        RegisterTheme(services);
        RegisterDesktop(services);
        return services;
    }

    static partial void RegisterShell(IServiceCollection services);
    static partial void RegisterMail(IServiceCollection services);
    static partial void RegisterCompose(IServiceCollection services);
    static partial void RegisterOnboarding(IServiceCollection services);
    static partial void RegisterAccounts(IServiceCollection services);
    static partial void RegisterSettings(IServiceCollection services);
    static partial void RegisterCalendar(IServiceCollection services);
    static partial void RegisterContacts(IServiceCollection services);
    static partial void RegisterTasks(IServiceCollection services);
    static partial void RegisterIntelligence(IServiceCollection services);
    static partial void RegisterDialogs(IServiceCollection services);
    static partial void RegisterTheme(IServiceCollection services);
    static partial void RegisterDesktop(IServiceCollection services);
}
