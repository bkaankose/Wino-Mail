using Microsoft.Extensions.DependencyInjection;

namespace Wino.Mail.Linux.Infrastructure;

internal static partial class LinuxViewModelRegistration
{
    static partial void RegisterShell(IServiceCollection services)
    {
        services.AddSingleton<WindowStateStore>();
    }
}
