using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views;
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

    public Task<bool> OpenMailFileAsync(string path, byte[] content)
    {
        Serilog.Log.Information("Opening a message file is not available on Linux yet.");
        return Task.FromResult(false);
    }
}

/// <summary>The head side of the shared startup order: lifecycle hooks, then the shell, or the first account.</summary>
internal sealed class GtkApplicationLifecycleHost(IServiceProvider services, IDispatcher dispatcher, Func<MainWindow?> window, LaunchOptions options) : IApplicationLifecycleHost
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
            Task show = Task.CompletedTask;
            await dispatcher.ExecuteOnUIThread(() => show = navigation.ShowShellAsync());
            await show;
            return;
        }

#if DEBUG
        // Account setup has no screen yet. A Debug build adds one leased lab account instead; the account-created
        // message then brings up the shell exactly as the setup page's does.
        if (!string.IsNullOrWhiteSpace(options.LabLeasePath))
        {
            await dispatcher.ExecuteOnUIThread(() => window()?.ShowStatus("mail-send-receive-symbolic", "Adding the lab account", null, busy: true));
            await LabAccountBootstrap.AddAsync(services, dispatcher, options.LabLeasePath, options.LabServer ?? "stalwart");
            return;
        }
#else
        if (!string.IsNullOrWhiteSpace(options.LabLeasePath)) Serilog.Log.Warning("--lab-lease is ignored outside Debug builds.");
#endif
        await dispatcher.ExecuteOnUIThread(() => window()?.ShowStatus("avatar-default-symbolic", "No account yet",
            "Account setup is not part of the Linux proof of concept. Start a Debug build with --lab-lease <lease file> to add a lab account."));
    }

    public async Task StoppingAsync(bool runtimeStarted)
    {
        foreach (var hook in Hooks.Reverse()) await hook.StoppingAsync(runtimeStarted);
    }

    public Task StopNavigationAsync() => services.GetRequiredService<GtkNavigationService>().StopAsync();
}
