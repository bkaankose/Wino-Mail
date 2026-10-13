using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Account sub-page views and ViewModels (folder customization, linked inbox, account unread badges).</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterAccountExtrasSettingsViews(IServiceCollection services)
    {
        // Same types and transient lifetime as the WinUI registration.
        services.AddTransient<FolderCustomizationPageViewController>();
        services.AddTransient<MergedAccountDetailsPageViewController>();
        services.AddTransient<AccountUnreadBadgePageViewController>();
#if DEBUG
        AccountExtrasDebug.Register();
#endif
    }
}
