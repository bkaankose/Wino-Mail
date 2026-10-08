using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.MacOS.Views.Settings;
using Wino.Mail.ViewModels;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Account sub-page views and ViewModels (folder customization, linked inbox, account unread badges).</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterAccountExtrasSettingsViews(IServiceCollection services)
    {
        // Same types and transient lifetime as the WinUI registration.
        services.AddTransient<FolderCustomizationPageViewModel>();
        services.AddTransient<FolderCustomizationPageViewController>();
        services.AddTransient<MergedAccountDetailsPageViewModel>();
        services.AddTransient<MergedAccountDetailsPageViewController>();
        services.AddTransient<AccountUnreadBadgePageViewModel>();
        services.AddTransient<AccountUnreadBadgePageViewController>();
#if DEBUG
        AccountExtrasDebug.Register();
#endif
    }
}
