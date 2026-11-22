using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure;

internal static partial class LinuxViewModelRegistration
{
    static partial void RegisterSettings(IServiceCollection services)
    {
        services.RemoveAll<ISettingsWindowPresenter>();
        services.AddSingleton<SettingsWindowPresenter>();
        services.AddSingleton<ISettingsWindowPresenter>(provider => provider.GetRequiredService<SettingsWindowPresenter>());
    }
}
