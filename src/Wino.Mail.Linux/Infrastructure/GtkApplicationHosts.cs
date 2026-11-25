using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Activation;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed class GtkApplicationShellHost(IServiceProvider services) : IApplicationShellHost
{
    private GtkNavigationService Navigation => services.GetRequiredService<GtkNavigationService>();

    public bool IsShellActive(WinoApplicationMode mode) => Navigation.IsShellReady && Navigation.ActiveMode == mode;

    public Task<bool> EnsureShellAsync(WinoApplicationMode mode, object? parameter = null) => Navigation.EnsureShellAsync(mode, parameter);

    public Task ShowSettingsAsync(WinoPage page, object? parameter)
        => services.GetService<ISettingsWindowPresenter>() is { } presenter ? presenter.ShowAsync(page, parameter) : Task.CompletedTask;

    public Task<bool> OpenMailFileAsync(string path, byte[] content) => Views.Mail.EmlReaderWindow.OpenAsync(services, path, content);
}

/// <summary>The head side of the shared startup order: lifecycle hooks, then the shell, or the first account.</summary>
internal sealed class GtkApplicationLifecycleHost(IServiceProvider services, IDispatcher dispatcher, LaunchOptions options) : IApplicationLifecycleHost
{
    private IEnumerable<IApplicationLifecycleHook> Hooks => services.GetServices<IApplicationLifecycleHook>();

    public async Task ServicesReadyAsync()
    {
        foreach (var hook in Hooks) await hook.ServicesReadyAsync();
    }

    public async Task ShowStartupAsync(ApplicationStartupTarget target)
    {
        var navigation = services.GetRequiredService<GtkNavigationService>();
        if (target == ApplicationStartupTarget.Shell)
        {
            await services.GetRequiredService<AppLockCoordinator>().WaitForUnlockAsync();
            Task show = Task.CompletedTask;
            await dispatcher.ExecuteOnUIThread(() => show = navigation.ShowShellAsync());
            await show;
#if DEBUG
            if (options.LabEnableDav && !string.IsNullOrWhiteSpace(options.LabLeasePath))
                await LabAccountBootstrap.EnableDavAsync(services, dispatcher, options.LabLeasePath, options.LabServer ?? "stalwart");
#endif
            return;
        }

#if DEBUG
        // A Debug build started with --lab-lease adds the leased lab account instead of showing Welcome; the
        // account-created message then brings up the shell exactly as the setup page's does.
        if (!string.IsNullOrWhiteSpace(options.LabLeasePath))
        {
            await dispatcher.ExecuteOnUIThread(() => navigation.MainWindow?.ShowStatus("mail-send-receive-symbolic", "Adding the lab account", null, busy: true));
            await LabAccountBootstrap.AddAsync(services, dispatcher, options.LabLeasePath, options.LabServer ?? "stalwart");
            return;
        }
#else
        if (!string.IsNullOrWhiteSpace(options.LabLeasePath)) Serilog.Log.Warning("--lab-lease is ignored outside Debug builds.");
#endif
        await navigation.NavigateAsync(WinoPage.WelcomePageV2);
    }

    public async Task StoppingAsync(bool runtimeStarted)
    {
        foreach (var hook in Hooks.Reverse()) await hook.StoppingAsync(runtimeStarted);
    }

    public Task StopNavigationAsync() => services.GetRequiredService<GtkNavigationService>().StopAsync();
}
