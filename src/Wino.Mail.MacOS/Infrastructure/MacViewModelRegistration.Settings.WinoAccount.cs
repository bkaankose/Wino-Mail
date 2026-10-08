using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Wino Account management views and ViewModels. Owned by the Wino Account page work (WS10).</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterWinoAccountSettingsViews(IServiceCollection services)
    {
        // WinoAccountManagementPageViewModel is registered with the other Settings ViewModels (shared with the Intelligence page).
        services.AddTransient<WinoAccountManagementPageViewController>();
#if DEBUG
        WinoAccountDebug.Register();
#endif
    }
}
