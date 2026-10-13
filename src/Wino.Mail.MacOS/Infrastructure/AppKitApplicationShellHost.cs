using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Activation;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// <see cref="IApplicationShellHost"/> over <see cref="AppKitNavigationService"/>: the shell window, the
/// Settings window and the read-only message window (<see cref="Views.Mail.EmlReaderWindow"/>).
/// </summary>
public sealed class AppKitApplicationShellHost(IServiceProvider services) : IApplicationShellHost
{
    private AppKitNavigationService Navigation => services.GetRequiredService<AppKitNavigationService>();

    public bool IsShellActive(WinoApplicationMode mode)
    {
        var navigation = Navigation;
        return navigation.IsShellReady && navigation.Shell?.ActiveMode == mode;
    }

    public Task<bool> EnsureShellAsync(WinoApplicationMode mode, object? parameter = null) => Navigation.EnsureShellAsync(mode, parameter);

    public async Task ShowSettingsAsync(WinoPage page, object? parameter)
    {
        var presenter = services.GetService<ISettingsWindowPresenter>();
        if (presenter is null)
        {
            Navigation.ChangeApplicationMode(WinoApplicationMode.Settings);
            return;
        }
        await presenter.ShowAsync(page, parameter);
    }

    public Task<bool> OpenMailFileAsync(string path, byte[] content) => Views.Mail.EmlReaderWindow.OpenAsync(services, path, content);
}
