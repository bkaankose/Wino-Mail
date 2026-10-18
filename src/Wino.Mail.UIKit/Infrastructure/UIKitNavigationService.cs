using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Navigation;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// UIKit host for the shared <see cref="NavigationCoordinator"/>, which owns routes, history, back confirmation and the
/// account lifecycle reactions (the Mac AppKitNavigationService). This class creates, activates and releases
/// controllers; the idiom's <see cref="IUIKitShellHost"/> places them. Routes come from the idiom's
/// <see cref="IUIKitPageRegistry"/>: Root pages replace the window root (or open in the add-account sheet while the
/// shell is up), ShellContent goes to the shell, RenderingFrame and Push to the reader frame or the current stack, and
/// Sheet/FullScreenSheet pages share one presented stack, which is the coordinator's Settings surface.
/// </summary>
public sealed class UIKitNavigationService : INavigationService, INavigationHost, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly IUIKitPageRegistry _registry;
    private readonly IUIKitShellHost _host;
    private readonly NavigationCoordinator _coordinator;
    private UIViewController? _current;
    private UIKitShellViewController? _shell;
    private UIViewController? _renderingPage;
    // The content page that shows rendering-frame pages in its own area (the Mac AttachRenderingHost).
    private IRenderingFrameHost? _renderingHost;
    // Onboarding opened while the shell is up (Settings › Add account): its own sheet, so the shell stays in place.
    private UINavigationController? _secondarySheet;
    private UIViewController? _secondaryPage;
    // Sheet pages (Settings and other Sheet/FullScreenSheet routes): one presented stack.
    private UINavigationController? _sheet;

    public UIKitNavigationService(IServiceProvider services, IDispatcher dispatcher, IUIKitPageRegistry registry, IUIKitShellHost host)
    {
        _services = services;
        _dispatcher = dispatcher;
        _registry = registry;
        _host = host;
        _coordinator = new NavigationCoordinator(this, services, dispatcher);
    }

    /// <summary>The shell while it is the window root.</summary>
    public UIKitShellViewController? Shell => _shell;

    public bool CanQuit => _coordinator.CanQuit;
    public bool IsShellReady => _coordinator.IsShellReady;

    public Type? GetPageType(WinoPage page) => _registry.TryGet(page, out var controller, out _) ? controller : null;

    public bool Navigate(WinoPage page, object? parameter = null, NavigationReferenceFrame? frame = null, NavigationTransitionType transition = NavigationTransitionType.None)
        => _coordinator.Navigate(page, parameter, frame, transition);

    public Task NavigateAsync(WinoPage page, object? parameter = null) => _coordinator.NavigateAsync(page, parameter);

    public Task ShowShellAsync() => _coordinator.ShowShellAsync();

    /// <inheritdoc cref="NavigationCoordinator.EnsureShellAsync"/>
    public Task<bool> EnsureShellAsync(WinoApplicationMode mode, object? parameter = null) => _coordinator.EnsureShellAsync(mode, parameter);

    public bool ChangeApplicationMode(WinoApplicationMode mode) => _coordinator.ChangeApplicationMode(mode);

    public bool ChangeApplicationMode(WinoApplicationMode mode, ShellModeActivationContext activationContext)
        => _coordinator.ChangeApplicationMode(mode, activationContext);

    public bool IsModeAvailable(WinoApplicationMode mode) => _coordinator.IsModeAvailable(mode);
    public bool ParkShell() => _coordinator.ParkShell();
    public bool RestoreShell(WinoApplicationMode mode) => _coordinator.RestoreShell(mode);
    public bool RestoreShell(WinoApplicationMode mode, ShellModeActivationContext activationContext) => _coordinator.RestoreShell(mode, activationContext);
    public bool CanGoBack() => _coordinator.CanGoBack();
    public void GoBack(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight) => _coordinator.GoBack(slideEffect);
    public Task<bool> GoBackAsync(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight) => _coordinator.GoBackAsync(slideEffect);
    public void SetNavigationResult(NavigationResult result) => _coordinator.SetNavigationResult(result);

    /// <summary>Opens Settings on <paramref name="page"/> (activation, notification and billing returns).</summary>
    public Task ShowSettingsPageAsync(WinoPage? page, object? parameter) => ((INavigationHost)this).ShowSettingsAsync(page, parameter);

    public Task StopAsync() => _coordinator.StopAsync();

    /// <summary>
    /// Forgets <paramref name="page"/> as the rendering-frame page without releasing it, because it left the frame alive
    /// (a reader or composer moved to its own window, or the frame emptied itself and released it). The next
    /// rendering-frame page then does not release it again.
    /// </summary>
    public void DetachRenderingPage(UIViewController page)
    {
        if (ReferenceEquals(_renderingPage, page)) _renderingPage = null;
    }

    /// <summary>
    /// Closes <paramref name="page"/> when it is on the presented sheet stack (Settings pages, the iPhone composer): the
    /// sheet closes with its pages released when the page is its root, otherwise the stack pops back to the page below
    /// it. Pages call this from their own Done, Cancel or Send so the sheet bookkeeping stays with the router.
    /// </summary>
    public async Task CloseSheetPageAsync(UIViewController page)
    {
        if (_sheet is not { } sheet) return;
        bool closeSheet = false;
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            var stack = sheet.ViewControllers ?? [];
            int index = Array.IndexOf(stack, page);
            if (index < 0) return;
            if (index == 0) closeSheet = true;
            else sheet.PopToViewController(stack[index - 1], true);
        });
        if (closeSheet) await CloseSheetAsync();
    }

    /// <summary>
    /// The shell's content page offers to show rendering-frame pages itself (<see cref="IRenderingFrameHost"/>; the Mac
    /// AppKitNavigationService.AttachRenderingHost). The page detaches on release.
    /// </summary>
    public void AttachRenderingHost(IRenderingFrameHost host) => _renderingHost = host;

    public void DetachRenderingHost(IRenderingFrameHost host)
    {
        if (ReferenceEquals(_renderingHost, host)) _renderingHost = null;
    }

    public void Dispose() => _coordinator.Dispose();

    #region INavigationHost

    bool INavigationHost.TryGetRoute(WinoPage page, object? parameter, out NavigationHostKind hostKind)
    {
        if (!_registry.TryGet(page, parameter, out _, out var host))
        {
            hostKind = default;
            return false;
        }
        hostKind = UIKitPageRegistry.ToHostKind(host);
        return true;
    }

    bool INavigationHost.IsShellShown => _shell is not null;
    bool INavigationHost.HasMainContent => _current is not null;
    bool INavigationHost.MainContentHasPendingWork => _current is IWinoViewController { HasPendingWork: true };
    object? INavigationHost.ShellContentViewModel => _shell?.ContentViewModel;
    bool INavigationHost.IsSecondaryWindowShown => _secondarySheet is not null;
    bool INavigationHost.IsSettingsAvailable => _sheet is not null || _registry.TryGet(WinoPage.SettingsPage, out _, out _);

    async Task INavigationHost.ShowInRenderingFrameAsync(WinoPage page, object? parameter)
    {
        if (!_registry.TryGet(page, parameter, out _, out var hostKind)) return;
        var controller = await CreateControllerAsync(page);
        var previous = hostKind == UIKitPageHost.RenderingFrame ? _renderingPage : null;
        bool shown = false;
        bool claimed = false;
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            // The content page's own frame first (the calendar composer); it then owns the page.
            if (hostKind == UIKitPageHost.RenderingFrame && _shell is not null && _renderingHost is { } pageHost && pageHost.TryShowInFrame(controller))
            {
                shown = claimed = true;
                return;
            }
            shown = hostKind == UIKitPageHost.Push || _shell is null
                ? _host.Push(_shell, controller)
                : _host.ShowInRenderingFrame(_shell, controller);
        });
        if (!shown) return;
        if (hostKind == UIKitPageHost.RenderingFrame && !claimed)
        {
            _renderingPage = controller;
            if (previous is IWinoViewController old && !ReferenceEquals(previous, controller)) await old.ReleaseAsync();
        }
        if (controller is IWinoViewController routed) await routed.ActivateAsync(NavigationMode.New, parameter);
    }

    async Task INavigationHost.SetShellContentAsync(WinoPage page, object? parameter)
    {
        if (_shell is not { } shell) return;
        // The live page of the same route takes the new parameter in place (a calendar date or view change), so its bar
        // and view state stay as they are.
        if (shell.Content is IReusableShellPage live && live.CanReuseFor(page))
        {
            await live.ReactivateAsync(parameter);
            return;
        }
        var controller = await CreateControllerAsync(page);
        await shell.SetContentAsync(controller, parameter);
    }

    Task INavigationHost.ClearShellContentAsync() => _shell?.ClearContentAsync() ?? Task.CompletedTask;

    async Task INavigationHost.ReplaceMainContentAsync(WinoPage page, object? parameter, NavigationMode mode, Action committed)
    {
        await _host.WindowReady;
        var controller = await CreateControllerAsync(page);
        var previous = _current;
        await _dispatcher.ExecuteOnUIThread(() => _host.ShowRoot(controller));
        committed();
        _current = controller;
        if (ReferenceEquals(previous, _shell))
        {
            _shell = null;
            _renderingPage = null;
            _renderingHost = null;
        }
        if (previous is IWinoViewController old) await old.ReleaseAsync();
        if (controller is IWinoViewController routed) await routed.ActivateAsync(mode, parameter);
    }

    async Task INavigationHost.ShowInSecondaryWindowAsync(WinoPage page, object? parameter, NavigationMode mode, Action committed)
    {
        var controller = await CreateControllerAsync(page);
        var previous = _secondaryPage;
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_secondarySheet is null)
            {
                _secondarySheet = _host.PresentSheet(controller, UIKitPageHost.Sheet, _coordinator.OnSecondaryWindowClosedByUser);
                _secondarySheet.NavigationBarHidden = true;
            }
            else
            {
                _secondarySheet.SetViewControllers([controller], false);
            }
        });
        committed();
        _secondaryPage = controller;
        if (previous is IWinoViewController old) await old.ReleaseAsync();
        if (controller is IWinoViewController routed) await routed.ActivateAsync(mode, parameter);
    }

    async Task INavigationHost.CloseSecondaryWindowAsync(bool closedByUser)
    {
        var sheet = _secondarySheet;
        var page = _secondaryPage;
        if (sheet is null) return;
        _secondarySheet = null;
        _secondaryPage = null;
        if (page is IWinoViewController routed) await routed.ReleaseAsync();
        if (!closedByUser)
        {
            Task dismissal = Task.CompletedTask;
            await _dispatcher.ExecuteOnUIThread(() => dismissal = _host.DismissSheetAsync(sheet));
            await dismissal;
        }
    }

    async Task<Func<Task>> INavigationHost.PresentShellAsync(Action committed)
    {
        await _host.WindowReady;
        UIKitShellViewController? created = null;
        await _dispatcher.ExecuteOnUIThread(() => created = _host.CreateShell());
        if (created is not { } shell) throw new InvalidOperationException("The app shell could not be created.");
        var previous = _current;
        await _dispatcher.ExecuteOnUIThread(() => _host.ShowRoot(shell));
        _shell = shell;
        _current = shell;
        committed();
        if (previous is IWinoViewController old) await old.ReleaseAsync();
        return () => shell.ActivateAsync(NavigationMode.New, null);
    }

    // One window per scene: the shell is already in front whenever it exists.
    Task INavigationHost.BringShellToFrontAsync() => Task.CompletedTask;

    Task INavigationHost.ActivateShellModeAsync(WinoApplicationMode mode, ShellModeActivationContext? activationContext)
        => _shell?.ActivateModeAsync(mode, activationContext) ?? Task.CompletedTask;

    async Task INavigationHost.DismissShellSurfacesAsync()
    {
        _renderingPage = null;
        _renderingHost = null;
        await CloseSheetAsync();
    }

    async Task INavigationHost.ReleaseMainContentAsync()
    {
        if (_current is IWinoViewController controller) await controller.ReleaseAsync();
        _current = null;
        _shell = null;
        _renderingPage = null;
        _renderingHost = null;
    }

    async Task INavigationHost.ShowSettingsAsync(WinoPage? page, object? parameter)
    {
        var target = page ?? WinoPage.SettingsPage;
        if (!_registry.TryGet(target, parameter, out _, out var hostKind)) return;
        var controller = await CreateControllerAsync(target);
        // The iPhone composer is a sheet page: Send and Cancel close it through the router's sheet bookkeeping.
        if (controller is Views.Mail.ComposePageViewController compose) compose.CloseHandler = () => CloseSheetPageAsync(compose);
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            // A sheet dismissed outside the router (no swipe report) is gone; present a new one.
            if (_sheet is { PresentingViewController: null, IsBeingPresented: false }) _sheet = null;
            if (_sheet is { } sheet) sheet.PushViewController(controller, true);
            else _sheet = _host.PresentSheet(controller, hostKind == UIKitPageHost.FullScreenSheet ? UIKitPageHost.FullScreenSheet : UIKitPageHost.Sheet, SheetClosedByUser);
        });
        if (controller is IWinoViewController routed) await routed.ActivateAsync(NavigationMode.New, parameter);
    }

    // Pages inside the sheet release themselves when it is dismissed (WinoViewController.ViewDidDisappear).
    private void SheetClosedByUser() => _sheet = null;

    /// <summary>
    /// Back belongs to the presented sheet: it pops the sheet's top page, and when the sheet holds a single page (an
    /// editor route such as the contact editor, the iPhone composer or the Settings root) it closes the sheet instead of
    /// rewinding the shell history underneath. A sheet dismissed outside the router is dropped and back falls through.
    /// </summary>
    bool INavigationHost.TrySettingsGoBack()
    {
        if (_sheet is not { } sheet) return false;
        if (Foundation.NSThread.IsMain) return SheetGoBack(sheet);

        // Off the main thread (a ViewModel continuing after an await) the stack is read on the main queue; a stale
        // sheet then hands back to the coordinator, which finds no sheet and walks the history.
        _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            if (ReferenceEquals(_sheet, sheet) && !SheetGoBack(sheet)) _coordinator.GoBack();
        });
        return true;
    }

    private bool SheetGoBack(UINavigationController sheet)
    {
        if (sheet is { PresentingViewController: null, IsBeingPresented: false })
        {
            if (ReferenceEquals(_sheet, sheet)) _sheet = null;
            return false;
        }
        if (sheet.ViewControllers is { Length: > 1 })
        {
            sheet.PopViewController(true);
            return true;
        }
        _ = CloseSheetAsync();
        return true;
    }

    Task INavigationHost.CloseSettingsAsync() => CloseSheetAsync();

    Task INavigationHost.StopSettingsAsync() => CloseSheetAsync();

    #endregion

    private async Task CloseSheetAsync()
    {
        var sheet = _sheet;
        if (sheet is null) return;
        _sheet = null;
        UIViewController[] pages = [];
        Task dismissal = Task.CompletedTask;
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            pages = sheet.ViewControllers ?? [];
            dismissal = _host.DismissSheetAsync(sheet);
        });
        await dismissal;
        foreach (var page in pages.OfType<IWinoViewController>()) await page.ReleaseAsync();
    }

    private async Task<UIViewController> CreateControllerAsync(WinoPage page)
    {
        if (!_registry.TryGet(page, out var type, out _))
            throw new NotSupportedException($"The UIKit route {page} has not been implemented.");
        UIViewController? created = null;
        await _dispatcher.ExecuteOnUIThread(() => created = (UIViewController)_services.GetRequiredService(type));
        return created ?? throw new InvalidOperationException($"The UIKit route {page} produced no controller.");
    }
}
