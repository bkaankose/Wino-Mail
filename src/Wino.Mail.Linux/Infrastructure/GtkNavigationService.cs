using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Views;
using Wino.Mail.Linux.Views.Shell;
using Wino.Mail.ViewModels.Navigation;
using Wino.Mail.ViewModels.Shell;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// The host of the shared <see cref="NavigationCoordinator"/>, which owns the rules. Routes come from
/// <see cref="LinuxPageRegistry"/>: a new page adds a route there and needs no change here. The reading pane belongs to
/// whoever attached it (the shell today). Settings and the secondary window are optional presenters; while none is
/// registered their members refuse.
/// </summary>
internal sealed class GtkNavigationService : INavigationService, INavigationHost
{
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly Func<MainWindow?> _window;
    private readonly LinuxPageRegistry _registry;
    private readonly NavigationCoordinator _coordinator;
    private readonly HashSet<string> _refused = [];
    private ShellView? _shell;
    private IWinoView? _main;
    private IRenderingFrameHost? _renderingHost;

    public GtkNavigationService(IServiceProvider services, IDispatcher dispatcher, Func<MainWindow?> window)
    {
        _services = services;
        _dispatcher = dispatcher;
        _window = window;
        _registry = services.GetRequiredService<LinuxPageRegistry>();
        _coordinator = new NavigationCoordinator(this, services, dispatcher, services.GetRequiredService<IMessenger>());
    }

    public bool IsShellReady => _coordinator.IsShellReady;
    public WinoApplicationMode? ActiveMode => _shell?.ActiveMode;
    public Task ShowShellAsync() => _coordinator.ShowShellAsync();
    public Task<bool> EnsureShellAsync(WinoApplicationMode mode, object? parameter = null) => _coordinator.EnsureShellAsync(mode, parameter);
    public Task StopAsync() => _coordinator.StopAsync();
    public void OnSecondaryWindowClosedByUser() => _coordinator.OnSecondaryWindowClosedByUser();
    public bool IsModeAvailable(WinoApplicationMode mode) => _coordinator.IsModeAvailable(mode);

    // ---- INavigationService: the coordinator decides ----

    public bool Navigate(WinoPage page, object? parameter = null, NavigationReferenceFrame? frame = null, NavigationTransitionType transition = NavigationTransitionType.None)
        => _coordinator.Navigate(page, parameter, frame, transition);
    public Type? GetPageType(WinoPage winoPage) => _registry.TryGet(winoPage, out var route) ? route.ViewType : null;
    public bool ChangeApplicationMode(WinoApplicationMode mode) => _coordinator.ChangeApplicationMode(mode);
    public bool ChangeApplicationMode(WinoApplicationMode mode, ShellModeActivationContext activationContext) => _coordinator.ChangeApplicationMode(mode, activationContext);
    public bool ParkShell() => _coordinator.ParkShell();
    public bool RestoreShell(WinoApplicationMode mode) => _coordinator.RestoreShell(mode);
    public bool RestoreShell(WinoApplicationMode mode, ShellModeActivationContext activationContext) => _coordinator.RestoreShell(mode, activationContext);
    public bool CanGoBack() => _coordinator.CanGoBack();
    public bool CanGoBackToPage() => _coordinator.CanGoBackToPage();
    public Task PendingNavigation => _coordinator.PendingNavigation;
    public WinoPage? BackPage => _coordinator.BackPage;
    public void GoBack(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight) => _coordinator.GoBack(slideEffect);
    public Task<bool> GoBackAsync(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight) => _coordinator.GoBackAsync(slideEffect);
    public void SetNavigationResult(NavigationResult result) => _coordinator.SetNavigationResult(result);

    public void AttachRenderingHost(IRenderingFrameHost host) => _renderingHost = host;

    public void DetachRenderingHost(IRenderingFrameHost host)
    {
        if (ReferenceEquals(_renderingHost, host)) _renderingHost = null;
    }

    // ---- INavigationHost ----

    public bool TryGetRoute(WinoPage page, object? parameter, out NavigationHostKind hostKind)
    {
        hostKind = default;
        if (!_registry.TryGet(page, parameter, out var route)) return false;
        switch (route.Host)
        {
            case LinuxPageHost.Window: hostKind = NavigationHostKind.Window; return true;
            case LinuxPageHost.ShellContent: hostKind = NavigationHostKind.ShellContent; return true;
            case LinuxPageHost.RenderingFrame: hostKind = NavigationHostKind.RenderingFrame; return true;
            case LinuxPageHost.Settings: hostKind = NavigationHostKind.Settings; return true;
            default: return false;
        }
    }

    public bool IsShellShown => _shell is not null;
    public ShellView? Shell => _shell;
    public MainWindow? MainWindow => _window();
    public bool HasMainContent => _main is not null;
    public bool MainContentHasPendingWork => _main is { HasPendingWork: true };
    public object? ShellContentViewModel => _shell?.ContentViewModel;
    public bool IsSecondaryWindowShown => SecondaryWindows is { IsShown: true };
    public bool IsSettingsAvailable => Settings is not null;

    private ISettingsWindowPresenter? Settings => _services.GetService<ISettingsWindowPresenter>();
    private ISecondaryWindowPresenter? SecondaryWindows => _services.GetService<ISecondaryWindowPresenter>();

    public Task<Func<Task>> PresentShellAsync(Action committed) => OnUIAsync(async () =>
    {
        var window = _window() ?? throw new InvalidOperationException("The window is not open.");
        var shell = PageScopes.Create<ShellView>(_services);
        try
        {
            window.ShowContent(shell.Root);
        }
        catch
        {
            await shell.ReleaseAsync();
            throw;
        }

        _shell = shell;
        committed();
        var previous = _main;
        _main = shell;
        if (previous is not null && !ReferenceEquals(previous, shell)) await previous.ReleaseAsync();
        // Not awaited here: the shell navigates while it activates, and the coordinator runs that outside its gate.
        return (Func<Task>)(() => shell.ActivateAsync(NavigationMode.New, null));
    });

    public Task ActivateShellModeAsync(WinoApplicationMode mode, ShellModeActivationContext? activationContext)
        => OnUIAsync(async () =>
        {
            if (_shell is not null) await _shell.ActivateModeAsync(mode, activationContext);
            return true;
        });

    public Task SetShellContentAsync(WinoPage page, object? parameter) => OnUIAsync(async () =>
    {
        if (_shell is not { } shell) return false;
        if (!_registry.TryGet(page, parameter, out var route)) throw new NotSupportedException($"The page {page} has no Linux route.");
        // The open message belongs to the folder that is leaving.
        await (_renderingHost?.ClearAsync() ?? Task.CompletedTask);
        await shell.SetContentAsync(route.Create(_services), parameter);
        return true;
    });

    public Task ClearShellContentAsync() => OnUIAsync(async () =>
    {
        await (_renderingHost?.ClearAsync() ?? Task.CompletedTask);
        if (_shell is not null) await _shell.ClearContentAsync();
        return true;
    });

    public Task ShowInRenderingFrameAsync(WinoPage page, object? parameter) => OnUIAsync(async () =>
    {
        if (_renderingHost is not { } host || !_registry.TryGet(page, parameter, out var route)) return false;
        if (ShowsIdle(page, parameter)) await host.ClearAsync();
        else await host.ShowAsync(page, parameter, () => route.Create(_services));
        return true;
    });

    public Task BringShellToFrontAsync() => OnUIAsync(() =>
    {
        _window()?.Window.Present();
        return Task.FromResult(true);
    });

    public Task ReplaceMainContentAsync(WinoPage page, object? parameter, NavigationMode mode, Action committed) => OnUIAsync(async () =>
    {
        // The coordinator replaces the content with the Welcome page without asking TryGetRoute first.
        if (!_registry.TryGet(page, parameter, out var route))
        {
            Serilog.Log.Warning("Navigation: the page {Page} has no Linux route; the main content stays.", page);
            return false;
        }

        var window = _window() ?? throw new InvalidOperationException("The window is not open.");
        var view = route.Create(_services);
        try
        {
            window.ShowContent(view.Root);
        }
        catch
        {
            await view.ReleaseAsync();
            throw;
        }

        committed();
        var previous = _main;
        if (previous is not null && ReferenceEquals(previous, _shell))
        {
            _shell = null;
            _renderingHost = null;
        }
        _main = view;
        if (previous is not null) await previous.ReleaseAsync();
        await view.ActivateAsync(mode, parameter);
        return true;
    });

    public Task ReleaseMainContentAsync() => OnUIAsync(async () =>
    {
        var main = _main;
        _main = null;
        _shell = null;
        _renderingHost = null;
        if (main is not null) await main.ReleaseAsync();
        return true;
    });

    public async Task DismissShellSurfacesAsync()
    {
        await OnUIAsync(async () =>
        {
            var host = _renderingHost;
            _renderingHost = null;
            if (host is not null) await host.ClearAsync();
            return true;
        });
        if (SecondaryWindows is { } secondary) await secondary.CloseAsync(closedByUser: false);
        await OnUIAsync(async () =>
        {
            // Until a Welcome route exists the replace above was refused and the shell is still the main content.
            if (_shell is not { } shell || !ReferenceEquals(_main, shell)) return false;
            _shell = null;
            _main = null;
            await shell.ReleaseAsync();
            return true;
        });
    }

    public Task ShowInSecondaryWindowAsync(WinoPage page, object? parameter, NavigationMode mode, Action committed)
    {
        if (SecondaryWindows is not { } presenter) return Refuse(nameof(ShowInSecondaryWindowAsync), "SH-07");
        return OnUIAsync(async () =>
        {
            if (!_registry.TryGet(page, parameter, out var route)) throw new NotSupportedException($"The page {page} has no Linux route.");
            await presenter.ShowAsync(page, route.Create(_services), parameter, mode, committed);
            return true;
        });
    }

    public Task CloseSecondaryWindowAsync(bool closedByUser)
        => SecondaryWindows is { } presenter ? presenter.CloseAsync(closedByUser) : Refuse(nameof(CloseSecondaryWindowAsync), "SH-07");

    public Task ShowSettingsAsync(WinoPage? page, object? parameter)
        => Settings is { } settings ? settings.ShowAsync(page, parameter) : Refuse(nameof(ShowSettingsAsync), "ST-02");

    public bool TrySettingsGoBack() => Settings is { } settings && settings.TryGoBack();
    public Task CloseSettingsAsync() => Settings?.CloseAsync() ?? Task.CompletedTask;
    public Task StopSettingsAsync() => Settings?.StopAsync() ?? Task.CompletedTask;

    // Kept from the proof of concept: the reader without a message has nothing to show, so the pane shows idle instead.
    private static bool ShowsIdle(WinoPage page, object? parameter) => page == WinoPage.MailRenderingPage && parameter is null;

    private Task Refuse(string member, string package)
    {
        var message = $"{member} has no host on Linux yet ({package}).";
        bool first;
        lock (_refused) first = _refused.Add(member);
        if (first) Serilog.Log.Warning("Navigation: {Message}", message);
        return Task.FromException(new NotSupportedException(message));
    }

    private Task<T> OnUIAsync<T>(Func<Task<T>> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _dispatcher.ExecuteOnUIThread(async () =>
        {
            try { completion.TrySetResult(await work()); }
            catch (Exception exception)
            {
                // The coordinator observes its navigations without reporting them; a page that fails must be visible in the log.
                Serilog.Log.Error(exception, "A navigation step failed.");
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }
}
