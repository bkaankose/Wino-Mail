using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Linux.Desktop;

namespace Wino.Mail.Linux.Infrastructure;

internal static partial class LinuxViewModelRegistration
{
    static partial void RegisterShortcuts(IServiceCollection services)
    {
        services.AddSingleton(provider => new ShortcutHost(provider, provider.GetRequiredService<IDispatcher>()));
        services.AddSingleton<IApplicationLifecycleHook>(provider => provider.GetRequiredService<ShortcutHost>());
        services.AddSingleton(provider => new SingleKeyShortcuts(provider.GetRequiredService<IConfigurationService>(),
            () => provider.GetRequiredService<LinuxDesktopProfile>().EffectiveStyle == LinuxWindowStyle.Tiling));
    }
}
