using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.MacOS.Views;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// The process router owns logical activation; native appearance events do not register recipients.
/// Routes come from <see cref="MacPageRegistry"/>; each route names the host that shows it.
/// </summary>
public sealed class AppKitNavigationService : INavigationService, IDisposable,
    IRecipient<BreadcrumbNavigationRequested>, IRecipient<BackBreadcrumNavigationRequested>, IRecipient<AccountCreatedMessage>,
    IRecipient<WelcomeImportCompletedMessage>, IRecipient<AccountRemovedMessage>
{
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly Func<NSViewController, Action?> _host;
    private readonly MacPageRegistry _registry;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stack<(WinoPage Page, object? Parameter)> _history = new();
    private NSViewController? _current;
    private NSViewController? _shellContent;
    private WinoAppShellViewController? _shell;
    private IRenderingFrameHost? _renderingHost;
    // Onboarding pages opened while the shell is up (Settings › Add account) use their own window
    // so the main window and its mail state stay in place.
    private NSWindow? _secondaryWindow;
    private NSViewController? _secondaryController;
    private NSObject? _secondaryClosing;
    private readonly Stack<(WinoPage Page, object? Parameter)> _secondaryHistory = new();
    private (WinoPage Page, object? Parameter) _secondaryRoute;
    private WinoPage _page;
    private object? _parameter;
    private NavigationResult? _result;
    private bool _stopping;
    private Task _pending = Task.CompletedTask;
    // Completes once the current shell finished its first mode activation (its menus and first route).
    private Task _shellActivated = Task.CompletedTask;
    public bool CanQuit => _page != WinoPage.AccountSetupProgressPage || _current is not IWinoViewController { HasPendingWork: true };

    public AppKitNavigationService(IServiceProvider services, IDispatcher dispatcher, Func<NSViewController, Action?> host)
    {
        _services = services; _dispatcher = dispatcher; _host = host;
        _registry = services.GetRequiredService<MacPageRegistry>();
        WeakReferenceMessenger.Default.Register<BreadcrumbNavigationRequested>(this);
        WeakReferenceMessenger.Default.Register<BackBreadcrumNavigationRequested>(this);
        WeakReferenceMessenger.Default.Register<AccountCreatedMessage>(this);
        WeakReferenceMessenger.Default.Register<WelcomeImportCompletedMessage>(this);
        WeakReferenceMessenger.Default.Register<AccountRemovedMessage>(this);
    }

    /// <summary>The shell window while it is shown; used to attach sheets and the toolbar.</summary>
    public WinoAppShellViewController? Shell => _shell;

    /// <summary>The active mail page attaches its reading pane here and detaches on release.</summary>
    public void AttachRenderingHost(IRenderingFrameHost host) => _renderingHost = host;

    public void DetachRenderingHost(IRenderingFrameHost host)
    {
        if (ReferenceEquals(_renderingHost, host)) _renderingHost = null;
    }

    private ISettingsWindowPresenter? SettingsPresenter => _services.GetService<ISettingsWindowPresenter>();

    public Type? GetPageType(WinoPage page) => _registry.TryGet(page, out var controller, out _) ? controller : null;

    public bool Navigate(WinoPage page, object? parameter = null, NavigationReferenceFrame? frame = null, NavigationTransitionType transition = NavigationTransitionType.None)
    {
        if (_stopping || !_registry.TryGet(page, parameter, out _, out var host)) return false;
        _pending = host switch
        {
            MacPageHost.RenderingFrame => ObserveAsync(ShowInRenderingFrameAsync(page, parameter)),
            MacPageHost.SettingsWindow => ObserveAsync(ShowInSettingsAsync(page, parameter)),
            _ => ObserveAsync(NavigateAsync(page, parameter))
        };
        return true;
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (Exception exception) { _services.GetRequiredService<IWinoLogger>().CaptureException(exception, nameof(AppKitNavigationService)); }
    }

    private async Task ShowInRenderingFrameAsync(WinoPage page, object? parameter)
    {
        var host = _renderingHost;
        if (host is null || _stopping) return;
        NSViewController controller = null!;
        await _dispatcher.ExecuteOnUIThread(() => controller = CreateController(page));
        await host.ShowAsync(controller, parameter);
    }

    private Task ShowInSettingsAsync(WinoPage page, object? parameter)
        => SettingsPresenter?.ShowAsync(page, parameter) ?? Task.CompletedTask;

    public Task NavigateAsync(WinoPage page, object? parameter = null)
    {
        if (_registry.TryGet(page, parameter, out _, out var host))
        {
            if (host == MacPageHost.RenderingFrame) return ShowInRenderingFrameAsync(page, parameter);
            if (host == MacPageHost.SettingsWindow) return ShowInSettingsAsync(page, parameter);
        }
        return NavigateOwnedAsync(page, parameter, NavigationMode.New, addHistory: true);
    }

    private async Task NavigateOwnedAsync(WinoPage page, object? parameter, NavigationMode mode, bool addHistory)
    {
        await _gate.WaitAsync();
        try
        {
            if (_stopping || !_registry.TryGet(page, parameter, out _, out var host))
            {
                if (parameter is NavigateMailFolderEventArgs folder) folder.FolderInitLoadAwaitTask?.TrySetResult(false);
                return;
            }
            NSViewController controller = null!;
            await _dispatcher.ExecuteOnUIThread(() => controller = CreateController(page));
            if (_shell is not null && host == MacPageHost.ShellContent)
            {
                await _shell.SetContentAsync(controller, parameter);
                _shellContent = controller;
                if (addHistory) _history.Push((_page, _parameter));
                _page = page; _parameter = parameter;
                return;
            }
            if (_shell is not null && host == MacPageHost.Window)
            {
                await ShowInSecondaryWindowAsync(controller, page, parameter, mode, addHistory);
                return;
            }
            await ReplaceWindowContentAsync(controller, page, parameter, mode, addHistory);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Hosts <paramref name="controller"/> as the main window content (switching windows when the host decides to). Call under the gate.</summary>
    private async Task ReplaceWindowContentAsync(NSViewController controller, WinoPage page, object? parameter, NavigationMode mode, bool addHistory)
    {
        var previous = _current;
        Action? releaseWindow = null;
        try { await _dispatcher.ExecuteOnUIThread(() => releaseWindow = _host(controller)); }
        catch
        {
            await _dispatcher.ExecuteOnUIThread(controller.Dispose);
            throw;
        }
        if (addHistory && previous != null) _history.Push((_page, _parameter));
        _page = page; _parameter = parameter;
        _current = controller;
        if (ReferenceEquals(previous, _shell))
        {
            _shell = null;
            _shellContent = null;
        }
        if (previous is IWinoViewController old) await old.ReleaseAsync();
        await _dispatcher.ExecuteOnUIThread(() => { previous?.Dispose(); releaseWindow?.Invoke(); });
        await ((IWinoViewController)controller).ActivateAsync(mode, parameter);
    }

    private async Task ShowInSecondaryWindowAsync(NSViewController controller, WinoPage page, object? parameter, NavigationMode mode, bool addHistory)
    {
        var previous = _secondaryController;
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_secondaryWindow is null)
            {
                _secondaryWindow = new WelcomeWindow(controller) { Title = Wino.Core.Domain.Translator.WelcomeWindow_AddAccountButton };
                _secondaryClosing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification,
                    _ => _pending = ObserveAsync(CloseSecondaryWindowAsync(fromWindow: true)), _secondaryWindow);
                _secondaryWindow.MakeKeyAndOrderFront(null);
            }
            else
            {
                _secondaryWindow.ContentViewController = controller;
                _secondaryWindow.MakeKeyAndOrderFront(null);
            }
        });
        if (addHistory && previous is not null) _secondaryHistory.Push(_secondaryRoute);
        _secondaryRoute = (page, parameter);
        _secondaryController = controller;
        if (previous is IWinoViewController old) await old.ReleaseAsync();
        if (previous is not null) await _dispatcher.ExecuteOnUIThread(previous.Dispose);
        await ((IWinoViewController)controller).ActivateAsync(mode, parameter);
    }

    private async Task CloseSecondaryWindowAsync(bool fromWindow = false)
    {
        var controller = _secondaryController;
        var window = _secondaryWindow;
        if (window is null) return;
        _secondaryController = null;
        _secondaryWindow = null;
        _secondaryHistory.Clear();
        if (controller is IWinoViewController page) await page.ReleaseAsync();
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_secondaryClosing is not null) { NSNotificationCenter.DefaultCenter.RemoveObserver(_secondaryClosing); _secondaryClosing.Dispose(); _secondaryClosing = null; }
            if (!fromWindow) window.Close();
            controller?.Dispose();
            // Dispose after the close notification has finished unwinding.
            window.BeginInvokeOnMainThread(window.Dispose);
        });
    }

    public async Task ShowShellAsync()
    {
        WinoAppShellViewController? activatedShell = null;
        TaskCompletionSource? activation = null;
        await _gate.WaitAsync();
        try
        {
            if (_stopping) return;
            if (_shell is not null) return;
            WinoAppShellViewController shell = null!;
            await _dispatcher.ExecuteOnUIThread(() => shell = _services.GetRequiredService<WinoAppShellViewController>());
            var previous = _current;
            Action? releaseWindow = null;
            try { await _dispatcher.ExecuteOnUIThread(() => releaseWindow = _host(shell)); }
            catch
            {
                await shell.ReleaseAsync();
                await _dispatcher.ExecuteOnUIThread(shell.Dispose);
                throw;
            }
            _shell = shell; _current = shell; _page = WinoPage.None; _parameter = null;
            _history.Clear();
            activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _shellActivated = activation.Task;
            if (previous is IWinoViewController old) await old.ReleaseAsync();
            await _dispatcher.ExecuteOnUIThread(() => { previous?.Dispose(); releaseWindow?.Invoke(); });
            activatedShell = shell;
        }
        catch
        {
            activation?.TrySetResult();
            throw;
        }
        finally { _gate.Release(); }
        // Mail shell initialization awaits its first folder route. That route uses this
        // same router, so it must be allowed to enter before initialization can finish.
        if (activatedShell is not null)
        {
            try { await activatedShell.ActivateAsync(NavigationMode.New, null); }
            finally { activation?.TrySetResult(); }
        }
    }

    /// <summary>True when the shell is shown and has finished its first mode activation.</summary>
    public bool IsShellReady => _shell is not null && _shellActivated.IsCompleted;

    /// <summary>
    /// Windows App.EnsureShellWindowAsync for OS activation (URL schemes, files, notifications, Dock menu):
    /// shows the shell if needed, waits until its first activation finished, brings the window to the
    /// front and switches to <paramref name="mode"/>. A non-null <paramref name="parameter"/> is handed
    /// to the mode's provider as <see cref="ShellModeActivationContext.Parameter"/>, also when the mode is
    /// already active. Returns false while stopping or while onboarding owns the main window.
    /// </summary>
    public async Task<bool> EnsureShellAsync(WinoApplicationMode mode, object? parameter = null)
    {
        if (_stopping || mode == WinoApplicationMode.Settings) return false;
        // Account setup in progress owns the main window; do not replace it.
        if (_shell is null && _current is IWinoViewController { HasPendingWork: true }) return false;
        await ShowShellAsync();
        await _shellActivated;
        if (_stopping || _shell is not { } shell) return false;
        await _dispatcher.ExecuteOnUIThread(() => BringToFront(shell.View.Window));
        await shell.ActivateModeAsync(mode, parameter is null ? null : new ShellModeActivationContext { Parameter = parameter });
        return true;
    }

    /// <summary>
    /// Activates the app and orders <paramref name="window"/> front, restoring it from the Dock if minimized.
    /// Brings back the Dock icon and menu bar when Wino was running in the background (Accessory policy).
    /// </summary>
    public static void BringToFront(NSWindow? window)
    {
        var application = NSApplication.SharedApplication;
        if (application.ActivationPolicy != NSApplicationActivationPolicy.Regular)
            application.ActivationPolicy = NSApplicationActivationPolicy.Regular;
        application.ActivateIgnoringOtherApps(true);
        if (window is null) return;
        if (window.IsMiniaturized) window.Deminiaturize(null);
        window.MakeKeyAndOrderFront(null);
    }

    private NSViewController CreateController(WinoPage page)
    {
        if (!_registry.TryGet(page, out var type, out _))
            throw new NotSupportedException($"The native route {page} has not been implemented.");
        return (NSViewController)_services.GetRequiredService(type);
    }

    public bool ChangeApplicationMode(WinoApplicationMode mode) => ChangeApplicationMode(mode, null!);

    public bool ChangeApplicationMode(WinoApplicationMode mode, ShellModeActivationContext activationContext)
    {
        if (_stopping) return false;
        if (mode == WinoApplicationMode.Settings)
        {
            if (SettingsPresenter is not { } settings) return false;
            _pending = ObserveAsync(settings.ShowAsync());
            return true;
        }
        _pending = ObserveAsync(ActivateModeAsync(mode, activationContext));
        return true;
    }

    /// <summary>Shows the shell if needed, then lets it activate the mode like the Windows WinoAppShell.</summary>
    private async Task ActivateModeAsync(WinoApplicationMode mode, ShellModeActivationContext? activationContext)
    {
        await ShowShellAsync();
        if (_shell is { } shell && !_stopping) await shell.ActivateModeAsync(mode, activationContext);
    }

    /// <summary>True when the mode's root page has a native route (feature registries add them as they are built).</summary>
    public bool IsModeAvailable(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Mail => _registry.TryGet(WinoPage.MailListPage, out _, out _),
        WinoApplicationMode.Calendar => _registry.TryGet(WinoPage.CalendarPage, out _, out _),
        WinoApplicationMode.Contacts => _registry.TryGet(WinoPage.ContactsPage, out _, out _),
        WinoApplicationMode.Tasks => _registry.TryGet(WinoPage.ToDoPage, out _, out _),
        WinoApplicationMode.Settings => SettingsPresenter is not null,
        _ => false
    };

    public bool ParkShell() => _shell is not null;
    public bool RestoreShell(WinoApplicationMode mode) => ChangeApplicationMode(mode);
    public bool RestoreShell(WinoApplicationMode mode, ShellModeActivationContext activationContext) => ChangeApplicationMode(mode, activationContext);
    public bool CanGoBack() => !_stopping && (_secondaryWindow is not null || _history.Count > 0);

    public void GoBack(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight)
    {
        if (_secondaryWindow is null && SettingsPresenter?.TryGoBack() == true) return;
        _pending = ObserveAsync(GoBackAsync(slideEffect));
    }

    public async Task<bool> GoBackAsync(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight)
    {
        if (_secondaryWindow is null && SettingsPresenter?.TryGoBack() == true) return true;
        if (_secondaryWindow is not null)
        {
            if (_secondaryHistory.Count == 0) { await CloseSecondaryWindowAsync(); return true; }
            var back = _secondaryHistory.Pop();
            await _gate.WaitAsync();
            try
            {
                NSViewController controller = null!;
                await _dispatcher.ExecuteOnUIThread(() => controller = CreateController(back.Page));
                await ShowInSecondaryWindowAsync(controller, back.Page, back.Parameter, NavigationMode.Back, addHistory: false);
            }
            finally { _gate.Release(); }
            return true;
        }
        if (!CanGoBack()) return false;
        // Same contract as the Windows NavigationService: the page on screen may refuse to leave.
        if (_shellContent is IViewModelHost { AssociatedViewModel: IConfirmBackNavigation confirm } && !await confirm.CanNavigateBackAsync())
        {
            _result = null;
            return false;
        }
        var route = _history.Pop();
        if (route.Page == WinoPage.None && _shell is not null)
        {
            await _shell.ClearContentAsync();
            _page = WinoPage.None; _parameter = null;
            return true;
        }
        await NavigateOwnedAsync(route.Page, route.Parameter, NavigationMode.Back, addHistory: false);
        var result = _result;
        _result = null;
        if (_shellContent is IViewModelHost { AssociatedViewModel: IBackNavigationAware aware })
            await _dispatcher.ExecuteOnUIThread(() => aware.OnNavigatedBack(route.Parameter!, result!));
        return true;
    }

    public void SetNavigationResult(NavigationResult result) => _result = result;
    public void Receive(BreadcrumbNavigationRequested message) => Navigate(message.PageType, message.Parameter);
    public void Receive(BackBreadcrumNavigationRequested message)
    {
        if (message.Result is not null) _result = message.Result;
        GoBack();
    }
    public void Receive(AccountCreatedMessage message) => _pending = ObserveAsync(AccountCreatedAsync(message.Account));

    private async Task AccountCreatedAsync(Wino.Core.Domain.Entities.Shared.MailAccount account)
    {
        // From Settings › Add account the shell already exists: just close the add-account window.
        if (_secondaryWindow is not null) await CloseSecondaryWindowAsync();
        await ShowShellAsync();
        // Windows App.Receive(AccountCreatedMessage): the runtime holds a new account's first full
        // mail, calendar, contacts and To Do synchronization until the host hands it over after the
        // shell is up. Without it only the shell's Inbox sync ran and calendar events never arrived.
        await _services.GetRequiredService<IApplicationRuntime>().SynchronizeCreatedAccountAsync(account);
    }
    public void Receive(WelcomeImportCompletedMessage message) => _pending = ObserveAsync(ShowShellAsync());

    public void Receive(AccountRemovedMessage message) => _pending = ObserveAsync(AccountRemovedAsync());

    /// <summary>
    /// Windows App.HandleAccountRemovedAsync: once the last account is gone while the shell is up
    /// (not during an onboarding rollback), every app window closes and the Welcome window returns.
    /// </summary>
    private async Task AccountRemovedAsync()
    {
        if (_stopping || _shell is null) return;
        var accounts = await _services.GetRequiredService<IAccountService>().GetAccountsAsync();
        if (accounts.Count > 0 || _stopping || _shell is null) return;
        _services.GetService<WelcomeWizardContext>()?.Reset();
        await ReturnToWelcomeAsync();
    }

    private async Task ReturnToWelcomeAsync()
    {
        if (SettingsPresenter is { } settings) await settings.CloseAsync();
        await CloseSecondaryWindowAsync();
        await _gate.WaitAsync();
        try
        {
            if (_stopping || _shell is null) return;
            NSViewController controller = null!;
            await _dispatcher.ExecuteOnUIThread(() => controller = CreateController(WinoPage.WelcomePageV2));
            // The shell and its window are released by the window switch; a new account creates a fresh shell.
            _history.Clear();
            await ReplaceWindowContentAsync(controller, WinoPage.WelcomePageV2, null, NavigationMode.New, addHistory: false);
            _history.Clear();
            _renderingHost = null;
            await _dispatcher.ExecuteOnUIThread(() => CloseAuxiliaryWindows(controller.View.Window));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Closes the remaining titled app windows (compose pop-outs, What's New, sign-in) except <paramref name="keep"/>.</summary>
    private static void CloseAuxiliaryWindows(NSWindow? keep)
    {
        // A one-shot snapshot of the app's windows; nothing keeps the array.
        foreach (var window in NSApplication.SharedApplication.DangerousWindows)
        {
            if (keep is not null && window.Handle == keep.Handle) continue;
            if (!window.IsVisible || window.SheetParent is not null || !window.StyleMask.HasFlag(NSWindowStyle.Titled)) continue;
            // Close (not PerformClose) so windows whose delegates guard closing still go; WillClose runs their cleanup.
            window.Close();
        }
    }

    public async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        await _pending;
        if (SettingsPresenter is { } settings) await settings.StopAsync();
        await CloseSecondaryWindowAsync();
        await _gate.WaitAsync();
        try
        {
            if (_current is IWinoViewController controller) await controller.ReleaseAsync();
            await _dispatcher.ExecuteOnUIThread(() => _current?.Dispose());
            _current = null; _shell = null; _renderingHost = null;
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => WeakReferenceMessenger.Default.UnregisterAll(this);
}
