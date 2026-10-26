using System;
using Microsoft.Extensions.DependencyInjection;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Calendar.ViewModels.Services;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Intelligence;
using Wino.Mail.ViewModels.Settings;
using Wino.Shell.ViewModels;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The one ViewModel registration list every application head calls. Lifetimes are the WinUI ones:
/// mode providers and page-level state that must survive window recreation are singletons, pages
/// are transient, and the app shell host is transient so a new window always gets a fresh shell.
/// Heads register only their views, dialogs, navigation and platform services on top of this.
/// </summary>
public static class WinoViewModelRegistration
{
    public static IServiceCollection RegisterWinoViewModels(this IServiceCollection services)
    {
        // Calendar state and formatting services the calendar ViewModels depend on.
        services.AddSingleton<IAccountCalendarStateService, AccountCalendarStateService>();
        services.AddSingleton<IDateContextProvider, SystemDateContextProvider>();
        services.AddSingleton<ICalendarRangeTextFormatter, CalendarRangeTextFormatter>();

        // Mode providers remain application services so background synchronization can keep
        // running; the app shell owns window-specific binding state and dies with its window.
        services.AddSingleton<MailAppShellViewModel>();
        services.AddSingleton<CalendarAppShellViewModel>();
        services.AddSingleton<SettingsMenuProvider>();
        services.AddSingleton<IStartupInitializer, SettingsCatalogInitializer>();
        services.AddSingleton<ContactsPageViewModel>();
        services.AddSingleton<ToDoPageViewModel>();
        services.AddSingleton<CalendarPageViewModel>();
        services.AddSingleton<IShellMenuProviderResolver>(provider => new ShellMenuProviderResolver(
            () => provider.GetRequiredService<IMailShellClient>(),
            () => provider.GetRequiredService<ICalendarShellClient>(),
            () => provider.GetRequiredService<ContactsPageViewModel>(),
            () => provider.GetRequiredService<ToDoPageViewModel>(),
            () => provider.GetRequiredService<SettingsMenuProvider>()));
        services.AddTransient<WinoAppShellViewModel>();
        services.AddSingleton<IMailShellClient>(provider => provider.GetRequiredService<MailAppShellViewModel>());
        services.AddSingleton<ICalendarShellClient>(provider => provider.GetRequiredService<CalendarAppShellViewModel>());

        // Breaks the calendar shell / calendar page constructor cycle and keeps the whole
        // calendar object graph out of a session that never opens the calendar.
        services.AddSingleton(provider => new Lazy<CalendarPageViewModel>(provider.GetRequiredService<CalendarPageViewModel>));

        // Onboarding.
        services.AddSingleton<WelcomeWizardContext>();
        services.AddTransient<WelcomeHostPageViewModel>();
        services.AddTransient<WelcomePageV2ViewModel>();
        services.AddTransient<ProviderSelectionPageViewModel>();
        services.AddTransient<AccountSetupProgressPageViewModel>();
        services.AddTransient<SpecialImapCredentialsPageViewModel>();
        services.AddTransient<ImapCalDavSettingsPageViewModel>();

        // Mail.
        services.AddTransient<MailListPageViewModel>();
        services.AddTransient<MailRenderingPageViewModel>();
        services.AddTransient<ComposePageViewModel>();
        services.AddTransient<IdlePageViewModel>();
        services.AddTransient<WinoIntelligenceHeaderPresenter>();
        services.AddTransient<TestPageViewModel>();

        // Calendar and contacts.
        services.AddTransient<EventDetailsPageViewModel>();
        services.AddTransient<CalendarEventComposePageViewModel>();
        services.AddTransient<ContactEditPageViewModel>();

        // Extras.
        services.AddTransient<DailyBriefingPanelViewModel>();
        services.AddTransient<WhatsNewPageViewModel>();

        // Settings shell and pages (Windows group order).
        services.AddTransient<SettingsDialogViewModel>();
        services.AddTransient<SettingsPageViewModel>();
        services.AddTransient<SettingOptionsPageViewModel>();
        services.AddTransient<AboutPageViewModel>();
        services.AddTransient<AppPreferencesPageViewModel>();
        services.AddTransient<PersonalizationPageViewModel>();
        services.AddTransient<ApplicationThemeGalleryPageViewModel>();
        services.AddTransient<ApplicationThemeEditorPageViewModel>();
        services.AddTransient<MailPreferencesPageViewModel>();
        services.AddTransient<MessageListPageViewModel>();
        services.AddTransient<ReadComposePanePageViewModel>();
        services.AddTransient<ReaderActionsPageViewModel>();
        services.AddTransient<NotificationSettingsPageViewModel>();
        services.AddTransient<UnreadBadgeSettingsPageViewModel>();
        services.AddTransient<AccountUnreadBadgePageViewModel>();
        services.AddTransient<KeyboardShortcutsPageViewModel>();
        services.AddTransient<BackupRestorePageViewModel>();
        services.AddTransient<CompanionSettingsPageViewModel>();
        services.AddTransient<StoragePageViewModel>();
        services.AddTransient<AccountManagementViewModel>();
        services.AddTransient<AccountDetailsPageViewModel>();
        services.AddTransient<MergedAccountDetailsPageViewModel>();
        services.AddTransient<FolderCustomizationPageViewModel>();
        services.AddTransient<AliasManagementPageViewModel>();
        services.AddTransient<MailCategoryManagementPageViewModel>();
        services.AddTransient<SignatureManagementPageViewModel>();
        services.AddTransient<SignatureAndEncryptionPageViewModel>();
        services.AddTransient<MailFiltersPageViewModel>();
        services.AddTransient<MailFilterEditorPageViewModel>();
        services.AddTransient<EmailTemplatesPageViewModel>();
        services.AddTransient<CreateEmailTemplatePageViewModel>();
        services.AddTransient<WinoAccountManagementPageViewModel>();
        services.AddTransient<WinoIntelligenceManagementPageViewModel>();
        services.AddTransient<IntelligenceCoveragePageViewModel>();
        services.AddTransient<CalendarPreferenceSettingsPageViewModel>();
        services.AddTransient<CalendarRenderingSettingsPageViewModel>();
        services.AddTransient<CalendarAccountSettingsPageViewModel>();
        services.AddTransient<ContactsPreferenceSettingsPageViewModel>();
        services.AddTransient<ToDoPreferenceSettingsPageViewModel>();
        return services;
    }
}
