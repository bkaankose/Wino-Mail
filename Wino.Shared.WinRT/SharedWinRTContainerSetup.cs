using Microsoft.Extensions.DependencyInjection;
using Windows.ApplicationModel.AppService;
using Wino.Domain.Interfaces;
using Wino.Shared.WinRT.Services;

namespace Wino.Shared.WinRT
{
    public static class SharedWinRTContainerSetup
    {
        public static void RegisterCoreUWPServices(this IServiceCollection services)
        {
            var serverConnectionManager = new WinoServerConnectionManager();

            services.AddSingleton<IWinoServerConnectionManager>(serverConnectionManager);
            services.AddSingleton<IWinoServerConnectionManager<AppServiceConnection>>(serverConnectionManager);

            services.AddSingleton<IUnderlyingThemeService, UnderlyingThemeService>();
            services.AddSingleton<INativeAppService, NativeAppService>();
            services.AddSingleton<IStoreManagementService, StoreManagementService>();
            services.AddSingleton<IAppShellService, AppShellService>();
            services.AddSingleton<IPreferencesService, PreferencesService>();
            services.AddTransient<IConfigurationService, ConfigurationService>();
            services.AddTransient<IFileService, FileService>();
            services.AddTransient<IStoreRatingService, StoreRatingService>();
            services.AddTransient<IKeyPressService, KeyPressService>();
            services.AddTransient<INotificationBuilder, NotificationBuilder>();
            services.AddTransient<IClipboardService, ClipboardService>();
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<IStatePersistanceService, StatePersistenceService>();
        }
    }
}
