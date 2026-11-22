using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Themes;

namespace Wino.Mail.Linux.Infrastructure;

internal static partial class LinuxViewModelRegistration
{
    static partial void RegisterTheme(IServiceCollection services)
    {
        services.AddSingleton(provider => new CustomThemeFileStore(provider.GetRequiredService<IApplicationConfiguration>()));
        services.AddSingleton(provider => new LinuxDesktopProfile(provider.GetRequiredService<IConfigurationService>()));
    }
}
