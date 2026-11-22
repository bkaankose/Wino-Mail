using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Linux.Security;

namespace Wino.Mail.Linux.Infrastructure;

internal static partial class LinuxViewModelRegistration
{
    static partial void RegisterWindows(IServiceCollection services)
    {
        services.AddSingleton(provider => new WindowManager(provider.GetRequiredService<IDispatcher>(), provider.GetRequiredService<WindowStateStore>()));
        services.AddSingleton<IApplicationLifecycleHook>(provider => provider.GetRequiredService<WindowManager>());
        services.AddSingleton(provider => new ReaderWindowRegistry(provider.GetRequiredService<WindowManager>()));
        services.AddSingleton<ISecondaryWindowPresenter>(provider => new GtkSecondaryWindowPresenter(
            provider, provider.GetRequiredService<IDispatcher>(), provider.GetRequiredService<WindowManager>()));
        services.AddSingleton(provider => new AppLockCoordinator(provider, provider.GetRequiredService<IAppLockService>(),
            provider.GetRequiredService<LinuxAppLockPlatform>(), provider.GetRequiredService<IDispatcher>()));
        services.AddSingleton<IApplicationLifecycleHook>(provider => provider.GetRequiredService<AppLockCoordinator>());
        services.AddSingleton(provider => new NotificationResponseHost(provider, provider.GetRequiredService<IDispatcher>()));
        services.AddSingleton<IApplicationLifecycleHook>(provider => provider.GetRequiredService<NotificationResponseHost>());
    }
}
