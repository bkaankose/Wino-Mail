using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Notifications;
using Wino.Mail.ViewModels.Activation;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Mail.ViewModels.Companion;

namespace Wino.Mail.WinUI;

/// <summary>
/// The Windows side of the shared startup and shutdown order (<see cref="ApplicationLifecycleCoordinator"/>) and the
/// shell host the shared notification and activation services navigate through. Startup runs once per process with
/// <see cref="ApplicationRuntimeStart.AfterFirstWindow"/>; launch, redirected and post-migration activations all wait
/// for that one run, and each then shows its window from its own activation route.
/// </summary>
public partial class App : IApplicationLifecycleHost, IApplicationShellHost
{
    private readonly Lock _startupLock = new();
    private Task? _startup;

    private INotificationResponseService NotificationResponses => Services.GetRequiredService<INotificationResponseService>();

    private Task EnsureCoreActivationInfrastructureAsync()
    {
        if (_activationInfrastructureInitialized)
            return Task.CompletedTask;

        lock (_startupLock)
        {
            return _startup ??= StartLifecycleAsync();
        }
    }

    private async Task StartLifecycleAsync()
    {
        try
        {
            _applicationRuntime = Services.GetRequiredService<IApplicationRuntime>();
            await Lifecycle.StartAsync(this, ApplicationRuntimeStart.AfterFirstWindow);
        }
        catch
        {
            // A failed start is retried by the next activation, as before.
            lock (_startupLock)
            {
                _startup = null;
            }

            throw;
        }
    }

    Task IApplicationLifecycleHook.ServicesReadyAsync()
    {
        _preferencesService = Services.GetRequiredService<IPreferencesService>();
        _accountService = Services.GetRequiredService<IAccountService>();
        return Task.CompletedTask;
    }

    async Task IApplicationLifecycleHost.ShowStartupAsync(ApplicationStartupTarget target)
    {
        _hasConfiguredAccounts = target == ApplicationStartupTarget.Shell;

        if (_companionIntegration != null)
        {
            await _companionIntegration.SetReadinessAsync(_hasConfiguredAccounts
                ? CompanionReadinessState.Ready
                : CompanionReadinessState.NoAccounts);
        }

        _activationInfrastructureInitialized = true;
    }

    // Both complete synchronously, so StopAsync begins the runtime stop before ExitApplicationAsync starts the draft shutdown.
    Task IApplicationLifecycleHook.StoppingAsync(bool runtimeStarted) => Task.CompletedTask;

    Task IApplicationLifecycleHost.StopNavigationAsync() => Task.CompletedTask;

    private Task HandleNotificationResponseAsync(NotificationResponse response)
        => NotificationResponses.HandleAsync(response with { RunsWithoutWindow = !HasShellWindow() });

    bool IApplicationShellHost.IsShellActive(WinoApplicationMode mode)
        => HasShellWindow() && Services.GetRequiredService<IStatePersistanceService>().ApplicationMode == mode;

    async Task<bool> IApplicationShellHost.EnsureShellAsync(WinoApplicationMode mode, object? parameter)
        => (await EnsureShellWindowAsync(mode, activateWindow: true, activationParameter: parameter)).ShellWindow != null;

    async Task<bool> IApplicationShellHost.EnsureShellForMessageAsync()
        => (await EnsureShellWindowAsync(WinoApplicationMode.Mail, activateWindow: true, applyThemeToExistingWindow: false)).ShellWindow != null;

    // The Windows Settings shell opens a page; it takes no page parameter.
    Task IApplicationShellHost.ShowSettingsAsync(WinoPage page, object? parameter) => OpenSettingsPageAsync(page);

    // Windows opens message files through its own file activation, not through the shared ActivationRouter.
    Task<bool> IApplicationShellHost.OpenMailFileAsync(string path, byte[] content) => Task.FromResult(false);
}
