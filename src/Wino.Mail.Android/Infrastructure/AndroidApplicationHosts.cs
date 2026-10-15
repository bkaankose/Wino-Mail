using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Activation;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.Android.Infrastructure;

/// <summary><see cref="IApplicationShellHost"/> over <see cref="AndroidNavigationService"/> (UIKitApplicationShellHost).</summary>
public sealed class AndroidApplicationShellHost(IServiceProvider services) : IApplicationShellHost
{
    private AndroidNavigationService Navigation => services.GetRequiredService<AndroidNavigationService>();

    public bool IsShellActive(WinoApplicationMode mode)
    {
        var navigation = Navigation;
        return navigation.IsShellReady && navigation.ActiveMode == mode;
    }

    public Task<bool> EnsureShellAsync(WinoApplicationMode mode, object? parameter = null) => Navigation.EnsureShellAsync(mode, parameter);

    public Task ShowSettingsAsync(WinoPage page, object? parameter) => Navigation.ShowSettingsPageAsync(page, parameter);

    /// <summary>The read-only message file reader arrives with the mail reader (T7).</summary>
    public Task<bool> OpenMailFileAsync(string path, byte[] content)
    {
        Serilog.Log.Information("Opening a message file is not available on Android yet.");
        return Task.FromResult(false);
    }
}

/// <summary>
/// The head side of the shared startup and shutdown order (<see cref="ApplicationLifecycleCoordinator"/>): the
/// registered <see cref="IApplicationLifecycleHook"/> services in order, the first page, and navigation shutdown. The
/// first page does not wait for an activity: the navigation service shows it when MainActivity attaches.
/// </summary>
public sealed class AndroidApplicationLifecycleHost(IServiceProvider services, IDispatcher dispatcher) : IApplicationLifecycleHost
{
    private IEnumerable<IApplicationLifecycleHook> Hooks => services.GetServices<IApplicationLifecycleHook>();

    public async Task ServicesReadyAsync()
    {
        foreach (var hook in Hooks) await hook.ServicesReadyAsync();
    }

    public async Task ShowStartupAsync(ApplicationStartupTarget target)
    {
        var navigation = services.GetRequiredService<AndroidNavigationService>();
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

    public Task StopNavigationAsync() => services.GetRequiredService<AndroidNavigationService>().StopAsync();
}
