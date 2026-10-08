using Microsoft.Extensions.DependencyInjection;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels;
using Wino.Mail.MacOS.Views;
using Wino.Mail.MacOS.Views.Mail;
using Wino.Mail.MacOS.Views.Onboarding;
using Wino.Mail.MacOS.Views.Settings;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Shell.ViewModels;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Shared registrations live here. Feature views and their ViewModels register in
/// MacViewModelRegistration.Mail.cs and MacViewModelRegistration.Settings.cs.
/// </summary>
public static partial class MacViewModelRegistration
{
    public static IServiceCollection RegisterMacViewModels(this IServiceCollection services)
    {
        services.AddSingleton<WelcomeWizardContext>();
        services.AddTransient<WelcomePageV2ViewModel>();
        services.AddTransient<WelcomeHostPageViewModel>();
        services.AddTransient<ProviderSelectionPageViewModel>();
        services.AddTransient<AccountSetupProgressPageViewModel>();
        services.AddTransient<SpecialImapCredentialsPageViewModel>();
        services.AddTransient<ImapCalDavSettingsPageViewModel>();
        services.AddSingleton<MailAppShellViewModel>();
        services.AddSingleton<IMailShellClient>(provider => provider.GetRequiredService<MailAppShellViewModel>());
        services.AddSingleton<CalendarAppShellViewModel>();
        services.AddSingleton<CalendarPageViewModel>();
        services.AddSingleton(provider => new Lazy<CalendarPageViewModel>(provider.GetRequiredService<CalendarPageViewModel>));
        services.AddSingleton<IDateContextProvider, SystemDateContextProvider>();
        services.AddSingleton<ICalendarRangeTextFormatter, CalendarRangeTextFormatter>();
        services.AddSingleton<ICalendarShellClient>(provider => provider.GetRequiredService<CalendarAppShellViewModel>());
        services.AddSingleton<IAccountCalendarStateService, Wino.Calendar.ViewModels.Services.AccountCalendarStateService>();
        services.AddSingleton<ContactsPageViewModel>();
        services.AddSingleton<ToDoPageViewModel>();
        services.AddSingleton<SettingsMenuProvider>();
        services.AddSingleton<IShellMenuProviderResolver>(provider => new ShellMenuProviderResolver(
            () => provider.GetRequiredService<IMailShellClient>(), () => provider.GetRequiredService<ICalendarShellClient>(),
            () => provider.GetRequiredService<ContactsPageViewModel>(), () => provider.GetRequiredService<ToDoPageViewModel>(),
            () => provider.GetRequiredService<SettingsMenuProvider>()));
        services.AddTransient<WinoAppShellViewModel>();
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
    static partial void RegisterActivationServices(IServiceCollection services);
    static partial void RegisterNotificationServices(IServiceCollection services);
}
