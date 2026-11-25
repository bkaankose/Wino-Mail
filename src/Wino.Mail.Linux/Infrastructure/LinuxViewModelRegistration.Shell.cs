using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Infrastructure.Updates;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.Linux.Infrastructure;

internal static partial class LinuxViewModelRegistration
{
    static partial void RegisterShell(IServiceCollection services)
    {
        services.AddSingleton<WindowStateStore>();
        services.AddSingleton<DailyBriefingPresenter>();
        services.AddSingleton<IDailyBriefingPresenter>(provider => provider.GetRequiredService<DailyBriefingPresenter>());
        services.AddSingleton<WhatsNewPresenter>();
        services.AddSingleton<IWhatsNewPresenter>(provider => provider.GetRequiredService<WhatsNewPresenter>());
        services.AddSingleton<IApplicationLifecycleHook>(provider => provider.GetRequiredService<WhatsNewPresenter>());
        services.AddSingleton<LanguageHook>();
        services.AddSingleton<IApplicationLifecycleHook>(provider => provider.GetRequiredService<LanguageHook>());
        services.AddSingleton<LinuxUpdateService>();
        services.AddSingleton<IApplicationLifecycleHook>(provider => provider.GetRequiredService<LinuxUpdateService>());
        services.AddSingleton<LinuxUpdatePresenter>();
        services.AddSingleton<IApplicationLifecycleHook>(provider => provider.GetRequiredService<LinuxUpdatePresenter>());
    }
}
