using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Views;
using Wino.Mail.Linux.Views.Mail;
using Wino.Mail.Linux.Views.Shell;
using Wino.Mail.ViewModels.Navigation;
using Wino.Mail.ViewModels.Shell;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Navigation for the proof of concept. The shared <see cref="NavigationCoordinator"/> owns the rules; this class is
/// its host and knows three pages: the mail list in the shell, and the reader and the idle page in the rendering
/// frame. Every other page has no route yet, which the coordinator logs and refuses.
/// </summary>
internal sealed class GtkNavigationService : INavigationService, INavigationHost
{
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly Func<MainWindow?> _window;
    private readonly NavigationCoordinator _coordinator;
    private ShellView? _shell;
    private MailListView? _mailList;

    public GtkNavigationService(IServiceProvider services, IDispatcher dispatcher, Func<MainWindow?> window)
    {
        _services = services;
        _dispatcher = dispatcher;
        _window = window;
        _coordinator = new NavigationCoordinator(this, services, dispatcher, services.GetRequiredService<IMessenger>());
    }

    public bool IsShellReady => _coordinator.IsShellReady;
    public WinoApplicationMode? ActiveMode => _shell?.ActiveMode;
    public Task ShowShellAsync() => _coordinator.ShowShellAsync();
    public Task<bool> EnsureShellAsync(WinoApplicationMode mode, object? parameter = null) => _coordinator.EnsureShellAsync(mode, parameter);
    public Task StopAsync() => _coordinator.StopAsync();

    // ---- INavigationService: the coordinator decides ----

    public bool Navigate(WinoPage page, object? parameter = null, NavigationReferenceFrame? frame = null, NavigationTransitionType transition = NavigationTransitionType.None)
        => _coordinator.Navigate(page, parameter, frame, transition);
    public Type? GetPageType(WinoPage winoPage) => null;
    public bool ChangeApplicationMode(WinoApplicationMode mode) => _coordinator.ChangeApplicationMode(mode);
    public bool ChangeApplicationMode(WinoApplicationMode mode, ShellModeActivationContext activationContext) => _coordinator.ChangeApplicationMode(mode, activationContext);
    public bool ParkShell() => _coordinator.ParkShell();
    public bool RestoreShell(WinoApplicationMode mode) => _coordinator.RestoreShell(mode);
    public bool RestoreShell(WinoApplicationMode mode, ShellModeActivationContext activationContext) => _coordinator.RestoreShell(mode, activationContext);
    public bool CanGoBack() => _coordinator.CanGoBack();
    public void GoBack(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight) => _coordinator.GoBack(slideEffect);
    public Task<bool> GoBackAsync(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight) => _coordinator.GoBackAsync(slideEffect);
    public void SetNavigationResult(NavigationResult result) => _coordinator.SetNavigationResult(result);

    // ---- INavigationHost ----

    public bool TryGetRoute(WinoPage page, object? parameter, out NavigationHostKind hostKind)
    {
        switch (page)
        {
            case WinoPage.MailListPage:
                hostKind = NavigationHostKind.ShellContent;
                return true;
            case WinoPage.MailRenderingPage or WinoPage.IdlePage:
                hostKind = NavigationHostKind.RenderingFrame;
                return true;
            default:
                hostKind = default;
                return false;
        }
    }

    public bool IsShellShown => _shell is not null;
    public bool HasMainContent => false;
    public bool MainContentHasPendingWork => false;
    public object? ShellContentViewModel => _mailList?.ViewModel;
    public bool IsSecondaryWindowShown => false;
    public bool IsSettingsAvailable => false;

    public Task<Func<Task>> PresentShellAsync(Action committed) => OnUIAsync(() =>
    {
        var window = _window() ?? throw new InvalidOperationException("The window is not open.");
        var shell = _shell ??= new ShellView(_services, _dispatcher, this);
        window.ShowContent(shell.Root);
        committed();
        return Task.FromResult<Func<Task>>(shell.ActivateAsync);
    });

    public Task ActivateShellModeAsync(WinoApplicationMode mode, ShellModeActivationContext? activationContext)
        => OnUIAsync(async () =>
        {
            if (_shell is not null) await _shell.ActivateModeAsync(mode, activationContext);
            return true;
        });

    public Task SetShellContentAsync(WinoPage page, object? parameter) => OnUIAsync(async () =>
    {
        if (_shell is null || page != WinoPage.MailListPage) return false;
        ReleaseMailList();
        // The open message belongs to the folder that is leaving.
        _shell.Reader.ShowIdle();
        _mailList = new MailListView(_services, _dispatcher);
        _shell.ShowMailList(_mailList);
        await _mailList.ActivateAsync(parameter);
        return true;
    });

    public Task ClearShellContentAsync() => OnUIAsync(() =>
    {
        ReleaseMailList();
        _shell?.Reader.ShowIdle();
        _shell?.ShowMailList(null);
        return Task.FromResult(true);
    });

    public Task ShowInRenderingFrameAsync(WinoPage page, object? parameter) => OnUIAsync(async () =>
    {
        if (_shell is null) return false;
        if (page == WinoPage.MailRenderingPage && parameter is not null) await _shell.Reader.ShowAsync(parameter);
        else _shell.Reader.ShowIdle();
        return true;
    });

    public Task BringShellToFrontAsync() => OnUIAsync(() =>
    {
        _window()?.Window.Present();
        return Task.FromResult(true);
    });

    public async Task DismissShellSurfacesAsync()
    {
        await ClearShellContentAsync();
        await OnUIAsync(async () =>
        {
            if (_shell is not null) await _shell.ReleaseAsync();
            _shell = null;
            return true;
        });
    }

    // No welcome, setup, settings or secondary-window pages exist yet; the coordinator never routes to them (TryGetRoute).
    public Task ReplaceMainContentAsync(WinoPage page, object? parameter, NavigationMode mode, Action committed) => Task.CompletedTask;
    public Task ShowInSecondaryWindowAsync(WinoPage page, object? parameter, NavigationMode mode, Action committed) => Task.CompletedTask;
    public Task CloseSecondaryWindowAsync(bool closedByUser) => Task.CompletedTask;
    public Task ReleaseMainContentAsync() => Task.CompletedTask;
    public Task ShowSettingsAsync(WinoPage? page, object? parameter) => Task.CompletedTask;
    public bool TrySettingsGoBack() => false;
    public Task CloseSettingsAsync() => Task.CompletedTask;
    public Task StopSettingsAsync() => Task.CompletedTask;

    private void ReleaseMailList()
    {
        _mailList?.Release();
        _mailList = null;
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
