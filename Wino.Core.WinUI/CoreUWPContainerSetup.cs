using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.AppService;
using Wino.Core.Domain.Interfaces;
using Wino.Core.WinUI.Services;
using Wino.Core.ViewModels;
using Wino.Services;

namespace Wino.Core.WinUI;

public static class CoreUWPContainerSetup
{
    public static void RegisterCoreUWPServices(this IServiceCollection services)
    {
        var serverConnectionManager = new WinoServerConnectionManager();

        services.AddSingleton<IWinoServerConnectionManager>(serverConnectionManager);
        services.AddSingleton<IWinoServerConnectionManager<AppServiceConnection>>(serverConnectionManager);
        services.AddSingleton<IApplicationResourceManager<ResourceDictionary>, ApplicationResourceManager>();

        services.AddSingleton<IUnderlyingThemeService, UnderlyingThemeService>();
        services.AddSingleton<INativeAppService, NativeAppService>();
        services.AddSingleton<IStoreManagementService, StoreManagementService>();
        services.AddSingleton<IPreferencesService, PreferencesService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IStatePersistanceService, StatePersistenceService>();
        services.AddSingleton<IThumbnailService, ThumbnailService>();
        services.AddSingleton<IDialogServiceBase, DialogServiceBase>();
        services.AddTransient<IConfigurationService, ConfigurationService>();
        services.AddTransient<IFileService, FileService>();
        services.AddTransient<IStoreRatingService, StoreRatingService>();
        services.AddTransient<IKeyPressService, KeyPressService>();
        services.AddTransient<INotificationBuilder, NotificationBuilder>();
        services.AddTransient<IClipboardService, ClipboardService>();
        services.AddTransient<IStartupBehaviorService, StartupBehaviorService>();
        services.AddSingleton<IPrintService, PrintService>();

    }

    public static void RegisterCoreViewModels(this IServiceCollection services)
    {
        services.AddTransient(typeof(SettingsDialogViewModel));
        services.AddTransient(typeof(PersonalizationPageViewModel));
        services.AddTransient(typeof(SettingOptionsPageViewModel));
        services.AddTransient(typeof(AboutPageViewModel));
        services.AddTransient(typeof(SettingsPageViewModel));
        services.AddTransient(typeof(ManageAccountsPagePageViewModel));
    }
}
