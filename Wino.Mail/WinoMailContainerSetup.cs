using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Wino.Domain.Interfaces;
using Wino.Services;

namespace Wino.Mail
{
    public static class WinoMailContainerSetup
    {
        public static void RegisterWinoMailServices(this IServiceCollection services)
        {
            services.AddSingleton<IApplicationResourceManager<ResourceDictionary>, ApplicationResourceManager>();
            services.AddSingleton<ILaunchProtocolService, LaunchProtocolService>();
            services.AddSingleton<IWinoNavigationService, WinoNavigationService>();
            services.AddSingleton<IDialogService, DialogService>();
        }
    }
}
