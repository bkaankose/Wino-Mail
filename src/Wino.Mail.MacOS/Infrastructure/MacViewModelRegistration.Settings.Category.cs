using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.MacOS.Views.Settings;
using Wino.Mail.ViewModels;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Mail category management views and ViewModels. Owned by the core dialogs work (WS4).</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterCategorySettingsViews(IServiceCollection services)
    {
        // Same type and transient lifetime as the WinUI registration.
        services.AddTransient<MailCategoryManagementPageViewModel>();
        services.AddTransient<MailCategoryManagementPageViewController>();
#if DEBUG
        DialogsDebug.Register();
#endif
    }
}
