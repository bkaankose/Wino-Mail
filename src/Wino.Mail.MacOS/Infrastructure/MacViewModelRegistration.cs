using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.MacOS.Views;
using Wino.Mail.MacOS.Views.Onboarding;
using Wino.Mail.ViewModels;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// ViewModels come from the shared <see cref="WinoViewModelRegistration"/> list. Only native view
/// controllers and head presenters register here; feature controllers in their partial files.
/// </summary>
public static partial class MacViewModelRegistration
{
    public static IServiceCollection RegisterMacViewModels(this IServiceCollection services)
    {
        services.RegisterWinoViewModels();
        services.AddTransient<WelcomePageV2ViewController>();
        services.AddTransient<ProviderSelectionPageViewController>();
        services.AddTransient<AccountSetupProgressPageViewController>();
        services.AddTransient<SpecialImapCredentialsPageViewController>();
        services.AddTransient<ImapCalDavSettingsPageViewController>();
#if DEBUG
        OnboardingDebug.Register();
#endif
        services.AddTransient<WinoAppShellViewController>();
        services.AddSingleton<MacPageRegistry>();
        RegisterMailViews(services);
        RegisterSettingsViews(services);
        RegisterCalendarViews(services);
        RegisterTasksViews(services);
        RegisterContactsViews(services);
        RegisterShellExtrasViews(services);
        RegisterCalendarSettingsViews(services);
        RegisterCategorySettingsViews(services);
        RegisterSmimeSettingsViews(services);
        RegisterBackupSettingsViews(services);
        RegisterWinoAccountSettingsViews(services);
        RegisterCompanionSettingsViews(services);
        RegisterAccountExtrasSettingsViews(services);
        RegisterSignatureSettingsViews(services);
        RegisterFilterSettingsViews(services);
        RegisterTemplateSettingsViews(services);
        RegisterActivationServices(services);
        RegisterNotificationServices(services);
        return services;
    }

    static partial void RegisterMailViews(IServiceCollection services);
    static partial void RegisterSettingsViews(IServiceCollection services);
    static partial void RegisterCalendarViews(IServiceCollection services);
    static partial void RegisterTasksViews(IServiceCollection services);
    static partial void RegisterContactsViews(IServiceCollection services);
    static partial void RegisterShellExtrasViews(IServiceCollection services);

    // Feature registrations being ported in their own partial files. These run last, so a
    // registration here replaces an earlier one for the same service.
    static partial void RegisterCalendarSettingsViews(IServiceCollection services);
    static partial void RegisterCategorySettingsViews(IServiceCollection services);
    static partial void RegisterSmimeSettingsViews(IServiceCollection services);
    static partial void RegisterBackupSettingsViews(IServiceCollection services);
    static partial void RegisterWinoAccountSettingsViews(IServiceCollection services);
    static partial void RegisterCompanionSettingsViews(IServiceCollection services);
    static partial void RegisterAccountExtrasSettingsViews(IServiceCollection services);
    static partial void RegisterSignatureSettingsViews(IServiceCollection services);
    static partial void RegisterFilterSettingsViews(IServiceCollection services);
    static partial void RegisterTemplateSettingsViews(IServiceCollection services);
    static partial void RegisterActivationServices(IServiceCollection services);
    static partial void RegisterNotificationServices(IServiceCollection services);
}
