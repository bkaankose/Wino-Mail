using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.Linux.Infrastructure;

internal static partial class LinuxViewModelRegistration
{
    static partial void RegisterWindows(IServiceCollection services)
    {
        services.AddSingleton(provider => new WindowManager(provider.GetRequiredService<IDispatcher>(), provider.GetRequiredService<WindowStateStore>()));
        services.AddSingleton<IApplicationLifecycleHook>(provider => provider.GetRequiredService<WindowManager>());
        services.AddSingleton<ISecondaryWindowPresenter>(provider => new GtkSecondaryWindowPresenter(
            provider, provider.GetRequiredService<IDispatcher>(), provider.GetRequiredService<WindowManager>()));
    }
}
