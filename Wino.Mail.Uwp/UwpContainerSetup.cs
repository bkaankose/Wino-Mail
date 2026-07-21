using Microsoft.Extensions.DependencyInjection;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Services;
using Wino.Core.ViewModels;
using Wino.Core.WinUI.Services;
using Wino.Mail.Services;
using Wino.Mail.Uwp.Services;
using Wino.Mail.Uwp.Theming;
using Wino.Mail.Uwp.ViewModels;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Services;
using Windows.Storage;
using Windows.UI.Xaml;

namespace Wino.Mail.Uwp;

internal static class UwpContainerSetup
{
    public static ServiceProvider Build(
        CompanionConnectionService companion,
        UwpWindowPresentationManager windowPresentationManager)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRemoteBackendServices(companion);

        RegisterPresentationServices(services, windowPresentationManager);
        RegisterViewModels(services);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = false,
            ValidateOnBuild = false,
        });
    }

    private static void RegisterPresentationServices(
        IServiceCollection services,
        UwpWindowPresentationManager windowPresentationManager)
    {
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<IApplicationConfiguration>(_ => new ApplicationConfiguration
        {
            ApplicationDataFolderPath = ApplicationData.Current.LocalFolder.Path,
            PublisherSharedFolderPath = ApplicationData.Current
                .GetPublisherCacheFolder(ApplicationConfiguration.SharedFolderName).Path,
            ApplicationTempFolderPath = ApplicationData.Current.TemporaryFolder.Path,
        });
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<ITranslationService, TranslationService>();

        services.AddSingleton<IApplicationResourceManager<ResourceDictionary>, ApplicationResourceManager>();
        services.AddSingleton<WinUIDispatcher>();
        services.AddSingleton<IDispatcher>(provider => provider.GetRequiredService<WinUIDispatcher>());
        services.AddSingleton<IUnderlyingThemeService, UnderlyingThemeService>();
        services.AddSingleton(windowPresentationManager);
        services.AddSingleton<INewThemeService, NewThemeService>();
        services.AddSingleton<IStatePersistanceService, StatePersistenceService>();

        services.AddSingleton(provider =>
            provider.GetRequiredService<CompanionConnectionService>().NativeAppService);
        services.AddSingleton<INativeAppService>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<IAppMetadataService>(provider => provider.GetRequiredService<NativeAppService>());
        services.AddSingleton<IWinoLogger, WinoLogger>();
        services.AddSingleton<IWinoTelemetryService, WinoTelemetryService>();

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IMailDialogService, DialogService>();
        services.AddSingleton<IDialogServiceBase, DialogServiceBase>();
        services.AddSingleton<IAiActionOptionsService, AiActionOptionsService>();
        services.AddSingleton<IProviderService, ProviderService>();
        services.AddSingleton<IAccountCalendarStateService, AccountCalendarStateService>();
        services.AddSingleton<IDateContextProvider, SystemDateContextProvider>();
        services.AddSingleton<ICalendarRangeTextFormatter, CalendarRangeTextFormatter>();

        services.AddSingleton<IStoreManagementService, StoreManagementService>();
        services.AddSingleton<IStoreRatingService, StoreRatingService>();
        services.AddSingleton<IStoreUpdateService, StoreUpdateService>();
        services.AddSingleton<IStartupBehaviorService, StartupBehaviorService>();
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IKeyPressService, KeyPressService>();
        services.AddSingleton<IWebView2RuntimeValidatorService, WebView2RuntimeValidatorService>();
        services.AddSingleton<IPrintService, PrintService>();
        services.AddSingleton<ISmimeCertificateService, SmimeCertificateService>();
        services.AddSingleton<IThumbnailService, ThumbnailService>();
        services.AddSingleton<INotificationBuilder, CompanionNotificationService>();

        services.AddSingleton<IMimeFileService, MimeFileService>();
        services.AddSingleton<IShareActivationService, ShareActivationService>();
        services.AddSingleton<ILaunchProtocolService, LaunchProtocolService>();
        services.AddSingleton<IContextMenuItemService, ContextMenuItemService>();
        services.AddSingleton<ICalendarContextMenuItemService, CalendarContextMenuItemService>();
        services.AddSingleton<ISpecialImapProviderConfigResolver, SpecialImapProviderConfigResolver>();
        services.AddSingleton<IFontService, FontService>();
        services.AddSingleton<IUpdateManager, UpdateManager>();
        services.AddSingleton<ReleaseLocalAccountDataCleanupService>();
    }

    private static void RegisterViewModels(IServiceCollection services)
    {
        services.AddSingleton<MailAppShellViewModel>();
        services.AddSingleton<CalendarAppShellViewModel>();
        services.AddSingleton<ContactsShellClient>();
        services.AddSingleton<SettingsShellClient>();
        services.AddSingleton<WinoAppShellViewModel>();
        services.AddSingleton<IMailShellClient>(provider => provider.GetRequiredService<MailAppShellViewModel>());
        services.AddSingleton<ICalendarShellClient>(provider => provider.GetRequiredService<CalendarAppShellViewModel>());
        services.AddSingleton<IShellClient>(provider => provider.GetRequiredService<MailAppShellViewModel>());
        services.AddSingleton<IShellClient>(provider => provider.GetRequiredService<CalendarAppShellViewModel>());
        services.AddSingleton<IShellClient>(provider => provider.GetRequiredService<ContactsShellClient>());
        services.AddSingleton<IShellClient>(provider => provider.GetRequiredService<SettingsShellClient>());

        services.AddTransient<MailListPageViewModel>();
        services.AddTransient<MailRenderingPageViewModel>();
        services.AddTransient<AccountManagementViewModel>();
        services.AddTransient<WelcomePageV2ViewModel>();
        services.AddTransient<ProviderSelectionPageViewModel>();
        services.AddTransient<AccountSetupProgressPageViewModel>();
        services.AddTransient<SpecialImapCredentialsPageViewModel>();
        services.AddSingleton<WelcomeWizardContext>();
        services.AddTransient<ComposePageViewModel>();
        services.AddTransient<IdlePageViewModel>();
        services.AddTransient<ImapCalDavSettingsPageViewModel>();
        services.AddTransient<AccountDetailsPageViewModel>();
        services.AddTransient<FolderCustomizationPageViewModel>();
        services.AddTransient<SignatureManagementPageViewModel>();
        services.AddTransient<MessageListPageViewModel>();
        services.AddTransient<MailNotificationSettingsPageViewModel>();
        services.AddTransient<ReadComposePanePageViewModel>();
        services.AddTransient<MergedAccountDetailsPageViewModel>();
        services.AddTransient<AppPreferencesPageViewModel>();
        services.AddTransient<StoragePageViewModel>();
        services.AddTransient<WinoAccountManagementPageViewModel>();
        services.AddTransient<AliasManagementPageViewModel>();
        services.AddTransient<MailCategoryManagementPageViewModel>();
        services.AddTransient<ContactsPageViewModel>();
        services.AddTransient<SignatureAndEncryptionPageViewModel>();
        services.AddTransient<EmailTemplatesPageViewModel>();
        services.AddTransient<CreateEmailTemplatePageViewModel>();

        services.AddSingleton<CalendarPageViewModel>();
        services.AddTransient<CalendarRenderingSettingsPageViewModel>();
        services.AddTransient<CalendarNotificationSettingsPageViewModel>();
        services.AddTransient<CalendarPreferenceSettingsPageViewModel>();
        services.AddTransient<CalendarAccountSettingsPageViewModel>();
        services.AddTransient<EventDetailsPageViewModel>();
        services.AddTransient<CalendarEventComposePageViewModel>();

        services.AddTransient<SettingsDialogViewModel>();
        services.AddTransient<PersonalizationPageViewModel>();
        services.AddTransient<SettingOptionsPageViewModel>();
        services.AddTransient<AboutPageViewModel>();
        services.AddTransient<SettingsPageViewModel>();
        services.AddTransient<ManageAccountsPagePageViewModel>();
        services.AddTransient<WelcomeHostPageViewModel>();
        services.AddTransient<KeyboardShortcutsPageViewModel>();
    }
}
