using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.UIKit.Views.Extras;
using Wino.Mail.UIKit.Views.Mail;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.UIKit;

/// <summary>
/// Mail pages and the shell extras (Mac MacViewModelRegistration.Mail and .ShellExtras). Owned by the Mail pages
/// feature. <c>RegisterUIKitViews</c> calls <see cref="RegisterMailViews"/>; the idiom registries add the routes with
/// <see cref="UIKitMailPageRoutes.RegisterMailPages"/>.
/// </summary>
public static partial class UIKitViewRegistration
{
    static partial void RegisterMailViews(IServiceCollection services);

    static partial void RegisterMailViews(IServiceCollection services)
    {
        // Same transient lifetimes as the Mac and WinUI registrations; the reader and compose are reused by their hosts.
        services.AddTransient<MailRenderingPageViewController>();
        services.AddTransient<ComposePageViewController>();
        services.AddTransient<IdlePageViewController>();
        services.AddTransient<WhatsNewPageViewController>();

        services.AddSingleton<DailyBriefingPresenter>();
        services.AddSingleton<IUIKitDailyBriefingPresenter>(provider => provider.GetRequiredService<DailyBriefingPresenter>());
        services.AddSingleton<WhatsNewPresenter>();
        services.AddSingleton<IUIKitWhatsNewPresenter>(provider => provider.GetRequiredService<WhatsNewPresenter>());
        services.AddSingleton<IApplicationLifecycleHook>(provider => new ShellExtrasLifecycleHook(provider));
#if DEBUG
        services.AddSingleton<IApplicationLifecycleHook>(provider => new MailDebugCommands(provider));
#endif
    }
}
