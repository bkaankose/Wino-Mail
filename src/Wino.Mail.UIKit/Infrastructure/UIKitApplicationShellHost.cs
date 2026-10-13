using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Activation;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary><see cref="IApplicationShellHost"/> over <see cref="UIKitNavigationService"/> (the Mac AppKitApplicationShellHost).</summary>
public sealed class UIKitApplicationShellHost(IServiceProvider services) : IApplicationShellHost
{
    private UIKitNavigationService Navigation => services.GetRequiredService<UIKitNavigationService>();

    public bool IsShellActive(WinoApplicationMode mode)
    {
        var navigation = Navigation;
        return navigation.IsShellReady && navigation.Shell?.ActiveMode == mode;
    }

    public Task<bool> EnsureShellAsync(WinoApplicationMode mode, object? parameter = null) => Navigation.EnsureShellAsync(mode, parameter);

    public Task ShowSettingsAsync(WinoPage page, object? parameter) => Navigation.ShowSettingsPageAsync(page, parameter);

    /// <summary>A saved message (.eml) in the read-only reader sheet (Mac EmlReaderWindow).</summary>
    public Task<bool> OpenMailFileAsync(string path, byte[] content) => Views.Mail.EmlReaderSheet.OpenAsync(services, path, content);
}

/// <summary>
/// The head side of the shared startup and shutdown order (<see cref="ApplicationLifecycleCoordinator"/>): the head's
/// lifecycle hooks (notifications, background refresh, the debug bridge), the first page once the scene window exists,
/// and navigation shutdown. Hooks are the registered <see cref="IApplicationLifecycleHook"/> services, in order.
/// </summary>
public sealed class UIKitApplicationLifecycleHost(IServiceProvider services, IDispatcher dispatcher, IUIKitShellHost shellHost) : IApplicationLifecycleHost
{
    private IEnumerable<IApplicationLifecycleHook> Hooks => services.GetServices<IApplicationLifecycleHook>();

    public async Task ServicesReadyAsync()
    {
        foreach (var hook in Hooks) await hook.ServicesReadyAsync();
    }

    public async Task ShowStartupAsync(ApplicationStartupTarget target)
    {
        await shellHost.WindowReady;
        var navigation = services.GetRequiredService<UIKitNavigationService>();
        Task show = Task.CompletedTask;
        await dispatcher.ExecuteOnUIThread(() => show = target == ApplicationStartupTarget.Shell
            ? navigation.ShowShellAsync()
            : navigation.NavigateAsync(WinoPage.WelcomePageV2));
        await show;
    }

    public async Task StoppingAsync(bool runtimeStarted)
    {
        foreach (var hook in Hooks.Reverse()) await hook.StoppingAsync(runtimeStarted);
    }

    public Task StopNavigationAsync() => services.GetRequiredService<UIKitNavigationService>().StopAsync();
}
