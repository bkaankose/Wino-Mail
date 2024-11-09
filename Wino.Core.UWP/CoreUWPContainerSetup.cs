using Microsoft.Extensions.DependencyInjection;
using Windows.ApplicationModel.AppService;
using Wino.Core.Domain.Interfaces;
using Wino.Core.UWP.Services;
using Wino.Services;

namespace Wino.Core.UWP
{
    public static class CoreUWPContainerSetup
    {
        public static void RegisterCoreUWPServices(this IServiceCollection services)
        {
            var serverConnectionManager = new WinoServerConnectionManager();

            services.AddSingleton<IWinoServerConnectionManager>(serverConnectionManager);
            services.AddSingleton<IWinoServerConnectionManager<AppServiceConnection>>(serverConnectionManager);

            services.AddSingleton<IUnderlyingThemeService, UnderlyingThemeService>();
            services.AddSingleton<INativeAppService, NativeAppService>();
            services.AddSingleton<IStoreManagementService, StoreManagementService>();
            services.AddSingleton<IBackgroundTaskService, BackgroundTaskService>();


            services.AddTransient<IConfigurationService, ConfigurationService>();
            services.AddTransient<IFileService, FileService>();
            services.AddTransient<IStoreRatingService, StoreRatingService>();
            services.AddTransient<IKeyPressService, KeyPressService>();
            services.AddTransient<INotificationBuilder, NotificationBuilder>();
            services.AddTransient<IClipboardService, ClipboardService>();
            services.AddTransient<IStartupBehaviorService, StartupBehaviorService>();
            services.AddSingleton<IPrintService, PrintService>();
        }
    }
}
