using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.AppLock;
using Wino.Mail.Linux.Views.Settings;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Platform.Linux.Notifications;
using Wino.Platform.Linux.Security;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Shows the app lock (Windows ShellWindow ShowAppLock / AppLockServiceStateChanged). At start a locked session shows
/// the lock screen before the shell exists, so no mail data is ever in the window while locked; a session locked later
/// swaps the main window's content for the lock screen, hides the other windows and suspends the application actions
/// and the shortcut router until the PIN unlocks it. Activations wait for <see cref="Unlocked"/>.
/// </summary>
internal sealed class AppLockCoordinator(IServiceProvider services, IAppLockService appLock, LinuxAppLockPlatform platform, IDispatcher dispatcher)
    : IApplicationLifecycleHook
{
    private readonly List<string> _suspendedActions = [];
    private AppLockScreen? _screen;
    private TaskCompletionSource? _unlock;
    private bool _settingsWasVisible;
    private bool _stopped;

    public bool IsLocked => _screen is not null;

    internal AppLockScreen? Screen => _screen;

    /// <summary>Raised on the main thread once the session is unlocked again.</summary>
    public event Action? Unlocked;

    public Task ServicesReadyAsync()
    {
        var dialogs = services.GetRequiredService<GtkDialogService>();
        platform.Prompt = dialogs.ShowAppLockVerificationDialogAsync;
        appLock.StateChanged += StateChanged;
        return Task.CompletedTask;
    }

    public Task StoppingAsync(bool runtimeStarted)
    {
        _stopped = true;
        appLock.StateChanged -= StateChanged;
        platform.Prompt = null;
        _unlock?.TrySetCanceled();
        return Task.CompletedTask;
    }

    /// <summary>Completes at once when the session is unlocked; otherwise shows the lock screen and completes on unlock.</summary>
    public async Task WaitForUnlockAsync()
    {
        TaskCompletionSource? wait = null;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (!appLock.IsLocked) return;
            Lock();
            wait = _unlock;
        });
        if (wait is not null) await wait.Task;
    }

    private void StateChanged(object? sender, EventArgs args) => _ = dispatcher.ExecuteOnUIThread(() =>
    {
        if (_stopped) return;
        if (appLock.IsLocked && _screen is null) Lock();
        else if (!appLock.IsLocked && _screen is not null) CompleteUnlock();
    });

    private void Lock()
    {
        if (_screen is not null || services.GetService<GtkNavigationService>()?.MainWindow is not { } window) return;
        Serilog.Log.Information("App lock: the session is locked.");
        _unlock ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var screen = _screen = new AppLockScreen(appLock, dispatcher);
        screen.Unlocked += CompleteUnlock;
        window.ShowLock(screen.Root);
        services.GetService<WindowManager>()?.SetHidden(true);
        if (services.GetService<SettingsWindowPresenter>() is { IsVisible: true, Window: { } settings })
        {
            _settingsWasVisible = true;
            settings.Window.SetVisible(false);
        }
        if (services.GetService<ShortcutHost>()?.MainRouter is { } router) router.IsSuspended = true;
        SuspendActions();
        screen.FocusEntry();
    }

    private void CompleteUnlock()
    {
        if (_screen is not { } screen || appLock.IsLocked) return;
        Serilog.Log.Information("App lock: the session is unlocked.");
        _screen = null;
        services.GetService<GtkNavigationService>()?.MainWindow?.HideLock();
        screen.Dispose();
        services.GetService<WindowManager>()?.SetHidden(false);
        if (_settingsWasVisible && services.GetService<SettingsWindowPresenter>()?.Window is { } settings) settings.Window.Present();
        _settingsWasVisible = false;
        if (services.GetService<ShortcutHost>()?.MainRouter is { } router) router.IsSuspended = false;
        ResumeActions();
        var unlock = _unlock;
        _unlock = null;
        unlock?.TrySetResult();
        Unlocked?.Invoke();
    }

    /// <summary>Application actions (modes, preferences, search, sync…) would act behind the lock: they are disabled while it shows.</summary>
    private void SuspendActions()
    {
        if (Gio.Application.GetDefault() is not { } application) return;
        foreach (var name in application.ListActions())
        {
            if (name.StartsWith("debug-", StringComparison.Ordinal) || name == AppActions.Quit || name == LinuxNotificationActions.ActionName || application.LookupAction(name) is not Gio.SimpleAction action || !action.GetEnabled()) continue;
            action.SetEnabled(false);
            _suspendedActions.Add(name);
        }
    }

    private void ResumeActions()
    {
        var application = Gio.Application.GetDefault();
        foreach (var name in _suspendedActions)
            if (application?.LookupAction(name) is Gio.SimpleAction action) action.SetEnabled(true);
        _suspendedActions.Clear();
    }
}
