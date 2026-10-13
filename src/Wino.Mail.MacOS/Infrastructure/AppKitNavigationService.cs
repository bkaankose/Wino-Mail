using AppKit;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.MacOS.Views;
using Wino.Mail.ViewModels.Navigation;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// AppKit host for the shared <see cref="NavigationCoordinator"/>, which owns routes, history, back
/// confirmation and the account lifecycle reactions. This class only creates, hosts, activates and
/// releases native controllers. Routes come from <see cref="MacPageRegistry"/>; each route names the
/// host that shows it.
/// </summary>
public sealed class AppKitNavigationService : INavigationService, INavigationHost, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly Func<NSViewController, Action?> _host;
    private readonly MacPageRegistry _registry;
    private readonly NavigationCoordinator _coordinator;
    private NSViewController? _current;
    private NSViewController? _shellContent;
    private WinoAppShellViewController? _shell;
    private IRenderingFrameHost? _renderingHost;
    // Onboarding pages opened while the shell is up (Settings > Add account) use their own window
    // so the main window and its mail state stay in place.
    private NSWindow? _secondaryWindow;
    private NSViewController? _secondaryController;
    private NSObject? _secondaryClosing;

    public AppKitNavigationService(IServiceProvider services, IDispatcher dispatcher, Func<NSViewController, Action?> host)
    {
        _services = services; _dispatcher = dispatcher; _host = host;
        _registry = services.GetRequiredService<MacPageRegistry>();
        _coordinator = new NavigationCoordinator(this, services, dispatcher);
    }

    public bool CanQuit => _coordinator.CanQuit;

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
        => _coordinator.Navigate(page, parameter, frame, transition);

    public Task NavigateAsync(WinoPage page, object? parameter = null) => _coordinator.NavigateAsync(page, parameter);

    public Task ShowShellAsync() => _coordinator.ShowShellAsync();

    /// <summary>True when the shell is shown and has finished its first mode activation.</summary>
    public bool IsShellReady => _coordinator.IsShellReady;

    /// <inheritdoc cref="NavigationCoordinator.EnsureShellAsync"/>
    public Task<bool> EnsureShellAsync(WinoApplicationMode mode, object? parameter = null) => _coordinator.EnsureShellAsync(mode, parameter);

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

    public bool ChangeApplicationMode(WinoApplicationMode mode) => _coordinator.ChangeApplicationMode(mode);

    public bool ChangeApplicationMode(WinoApplicationMode mode, ShellModeActivationContext activationContext)
        => _coordinator.ChangeApplicationMode(mode, activationContext);

    /// <summary>True when the mode's root page has a native route (feature registries add them as they are built).</summary>
    public bool IsModeAvailable(WinoApplicationMode mode) => _coordinator.IsModeAvailable(mode);

    public bool ParkShell() => _coordinator.ParkShell();
    public bool RestoreShell(WinoApplicationMode mode) => _coordinator.RestoreShell(mode);
    public bool RestoreShell(WinoApplicationMode mode, ShellModeActivationContext activationContext) => _coordinator.RestoreShell(mode, activationContext);
    public bool CanGoBack() => _coordinator.CanGoBack();
    public void GoBack(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight) => _coordinator.GoBack(slideEffect);
    public Task<bool> GoBackAsync(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight) => _coordinator.GoBackAsync(slideEffect);
    public void SetNavigationResult(NavigationResult result) => _coordinator.SetNavigationResult(result);

    public Task StopAsync() => _coordinator.StopAsync();

    public void Dispose() => _coordinator.Dispose();

    #region INavigationHost

    bool INavigationHost.TryGetRoute(WinoPage page, object? parameter, out NavigationHostKind hostKind)
    {
        if (!_registry.TryGet(page, parameter, out _, out var host))
        {
            hostKind = default;
            return false;
        }
        hostKind = host switch
        {
            MacPageHost.ShellContent => NavigationHostKind.ShellContent,
            MacPageHost.RenderingFrame => NavigationHostKind.RenderingFrame,
            MacPageHost.SettingsWindow => NavigationHostKind.Settings,
            _ => NavigationHostKind.Window
        };
        return true;
    }

    bool INavigationHost.IsShellShown => _shell is not null;
    bool INavigationHost.HasMainContent => _current is not null;
    bool INavigationHost.MainContentHasPendingWork => _current is IWinoViewController { HasPendingWork: true };
    object? INavigationHost.ShellContentViewModel => (_shellContent as IViewModelHost)?.AssociatedViewModel;
    bool INavigationHost.IsSecondaryWindowShown => _secondaryWindow is not null;
    bool INavigationHost.IsSettingsAvailable => SettingsPresenter is not null;

    async Task INavigationHost.ShowInRenderingFrameAsync(WinoPage page, object? parameter)
    {
        var host = _renderingHost;
        if (host is null) return;
        var controller = await CreateControllerAsync(page);
        await host.ShowAsync(controller, parameter);
    }

    async Task INavigationHost.SetShellContentAsync(WinoPage page, object? parameter)
    {
        if (_shell is not { } shell) return;
        var controller = await CreateControllerAsync(page);
        await shell.SetContentAsync(controller, parameter);
        _shellContent = controller;
    }

    Task INavigationHost.ClearShellContentAsync() => _shell?.ClearContentAsync() ?? Task.CompletedTask;

    async Task INavigationHost.ReplaceMainContentAsync(WinoPage page, object? parameter, NavigationMode mode, Action committed)
    {
        var controller = await CreateControllerAsync(page);
        var previous = _current;
        Action? releaseWindow = null;
        try { await _dispatcher.ExecuteOnUIThread(() => releaseWindow = _host(controller)); }
        catch
        {
            await _dispatcher.ExecuteOnUIThread(controller.Dispose);
            throw;
        }
        committed();
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

    async Task INavigationHost.ShowInSecondaryWindowAsync(WinoPage page, object? parameter, NavigationMode mode, Action committed)
    {
        var controller = await CreateControllerAsync(page);
        var previous = _secondaryController;
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_secondaryWindow is null)
            {
                _secondaryWindow = new WelcomeWindow(controller) { Title = Wino.Core.Domain.Translator.WelcomeWindow_AddAccountButton };
                _secondaryClosing = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification,
                    _ => _coordinator.OnSecondaryWindowClosedByUser(), _secondaryWindow);
                _secondaryWindow.MakeKeyAndOrderFront(null);
            }
            else
            {
                _secondaryWindow.ContentViewController = controller;
                _secondaryWindow.MakeKeyAndOrderFront(null);
            }
        });
        committed();
        _secondaryController = controller;
        if (previous is IWinoViewController old) await old.ReleaseAsync();
        if (previous is not null) await _dispatcher.ExecuteOnUIThread(previous.Dispose);
        await ((IWinoViewController)controller).ActivateAsync(mode, parameter);
    }

    async Task INavigationHost.CloseSecondaryWindowAsync(bool closedByUser)
    {
        var controller = _secondaryController;
        var window = _secondaryWindow;
        if (window is null) return;
        _secondaryController = null;
        _secondaryWindow = null;
        if (controller is IWinoViewController page) await page.ReleaseAsync();
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_secondaryClosing is not null) { NSNotificationCenter.DefaultCenter.RemoveObserver(_secondaryClosing); _secondaryClosing.Dispose(); _secondaryClosing = null; }
            if (!closedByUser) window.Close();
            controller?.Dispose();
            // Dispose after the close notification has finished unwinding.
            window.BeginInvokeOnMainThread(window.Dispose);
        });
    }

    async Task<Func<Task>> INavigationHost.PresentShellAsync(Action committed)
    {
        WinoAppShellViewController? created = null;
        await _dispatcher.ExecuteOnUIThread(() => created = _services.GetRequiredService<WinoAppShellViewController>());
        if (created is not { } shell) throw new InvalidOperationException("The app shell could not be created.");
        var previous = _current;
        Action? releaseWindow = null;
        try { await _dispatcher.ExecuteOnUIThread(() => releaseWindow = _host(shell)); }
        catch
        {
            await shell.ReleaseAsync();
            await _dispatcher.ExecuteOnUIThread(shell.Dispose);
            throw;
        }
        _shell = shell; _current = shell;
        committed();
        if (previous is IWinoViewController old) await old.ReleaseAsync();
        await _dispatcher.ExecuteOnUIThread(() => { previous?.Dispose(); releaseWindow?.Invoke(); });
        return () => shell.ActivateAsync(NavigationMode.New, null);
    }

    Task INavigationHost.BringShellToFrontAsync()
    {
        if (_shell is not { } shell) return Task.CompletedTask;
        return _dispatcher.ExecuteOnUIThread(() => BringToFront(shell.View.Window));
    }

    Task INavigationHost.ActivateShellModeAsync(WinoApplicationMode mode, ShellModeActivationContext? activationContext)
        => _shell?.ActivateModeAsync(mode, activationContext) ?? Task.CompletedTask;

    async Task INavigationHost.DismissShellSurfacesAsync()
    {
        _renderingHost = null;
        var keep = _current;
        await _dispatcher.ExecuteOnUIThread(() => CloseAuxiliaryWindows(keep?.View.Window));
    }

    async Task INavigationHost.ReleaseMainContentAsync()
    {
        if (_current is IWinoViewController controller) await controller.ReleaseAsync();
        await _dispatcher.ExecuteOnUIThread(() => _current?.Dispose());
        _current = null; _shell = null; _renderingHost = null;
    }

    Task INavigationHost.ShowSettingsAsync(WinoPage? page, object? parameter)
        => SettingsPresenter?.ShowAsync(page, parameter) ?? Task.CompletedTask;

    bool INavigationHost.TrySettingsGoBack() => SettingsPresenter?.TryGoBack() == true;

    Task INavigationHost.CloseSettingsAsync() => SettingsPresenter?.CloseAsync() ?? Task.CompletedTask;

    Task INavigationHost.StopSettingsAsync() => SettingsPresenter?.StopAsync() ?? Task.CompletedTask;

    #endregion

    private async Task<NSViewController> CreateControllerAsync(WinoPage page)
    {
        if (!_registry.TryGet(page, out var type, out _))
            throw new NotSupportedException($"The native route {page} has not been implemented.");
        NSViewController? created = null;
        await _dispatcher.ExecuteOnUIThread(() => created = (NSViewController)_services.GetRequiredService(type));
        return created ?? throw new InvalidOperationException($"The native route {page} produced no controller.");
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
}
