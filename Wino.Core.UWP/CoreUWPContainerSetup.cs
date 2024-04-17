using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Core.UWP.Services;
using Wino.Services;

namespace Wino.Core.UWP
{
    public static class CoreUWPContainerSetup
    {
        public static void RegisterCoreUWPServices(this IServiceCollection services)
        {
            services.AddSingleton<IUnderlyingThemeService, UnderlyingThemeService>();
            services.AddSingleton<INativeAppService, NativeAppService>();
            services.AddSingleton<IStoreManagementService, StoreManagementService>();
            services.AddSingleton<IBackgroundTaskService, BackgroundTaskService>();

            services.AddTransient<IAppInitializerService, AppInitializerService>();
            services.AddTransient<IConfigurationService, ConfigurationService>();
            services.AddTransient<IFileService, FileService>();
            services.AddTransient<IStoreRatingService, StoreRatingService>();
            services.AddTransient<IKeyPressService, KeyPressService>();
            services.AddTransient<IBackgroundSynchronizer, BackgroundSynchronizer>();
            services.AddTransient<INotificationBuilder, NotificationBuilder>();
            services.AddTransient<IClipboardService, ClipboardService>();
        }
    }
}
