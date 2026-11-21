using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.Linux.Infrastructure;

internal static partial class LinuxViewModelRegistration
{
    static partial void RegisterShell(IServiceCollection services)
    {
        services.AddSingleton<WindowStateStore>();
        services.AddSingleton<LanguageHook>();
        services.AddSingleton<IApplicationLifecycleHook>(provider => provider.GetRequiredService<LanguageHook>());
    }
}
