using Microsoft.Extensions.DependencyInjection;
using Wino.Core.ViewModels;
using Wino.Mail.MacOS.Views.Extras;
using Wino.Mail.ViewModels;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Daily briefing, What's New and Wino Account presenters. Owned by the shell extras feature.</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterShellExtrasViews(IServiceCollection services)
    {
        // Same transient lifetimes as the WinUI registration; the presenters keep the instance they need.
        services.AddTransient<DailyBriefingPanelViewModel>();
        services.AddTransient<WhatsNewPageViewModel>();
        services.AddTransient<WhatsNewPageViewController>();

        services.AddSingleton<DailyBriefingPresenter>();
        services.AddSingleton<IDailyBriefingPresenter>(provider => provider.GetRequiredService<DailyBriefingPresenter>());
        services.AddSingleton<WhatsNewPresenter>();
        services.AddSingleton<IWhatsNewPresenter>(provider => provider.GetRequiredService<WhatsNewPresenter>());
        services.AddSingleton<WinoAccountPresenter>();
        services.AddSingleton<IWinoAccountPresenter>(provider => provider.GetRequiredService<WinoAccountPresenter>());
#if DEBUG
        ShellExtrasDebug.Register();
#endif
    }
}
