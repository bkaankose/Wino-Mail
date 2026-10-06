using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.MacOS.Views;
using Wino.Mail.MacOS.Views.Mail;
using Wino.Mail.MacOS.Views.Settings;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>The process router owns logical activation; native appearance events do not register recipients.</summary>
public sealed class AppKitNavigationService : INavigationService, IDisposable,
    IRecipient<BreadcrumbNavigationRequested>, IRecipient<BackBreadcrumNavigationRequested>, IRecipient<AccountCreatedMessage>,
    IRecipient<WelcomeImportCompletedMessage>
{
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly Action<NSViewController> _host;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stack<(WinoPage Page, object? Parameter)> _history = new();
    private NSViewController? _current;
    private WinoAppShellViewController? _shell;
    private WinoPage _page;
    private object? _parameter;
    private NavigationResult? _result;
    private bool _stopping;
    private Task _pending = Task.CompletedTask;
    public bool CanQuit => _page != WinoPage.AccountSetupProgressPage || _current is not IWinoViewController { HasPendingWork: true };

    public AppKitNavigationService(IServiceProvider services, IDispatcher dispatcher, Action<NSViewController> host)
    {
        _services = services; _dispatcher = dispatcher; _host = host;
        WeakReferenceMessenger.Default.Register<BreadcrumbNavigationRequested>(this);
        WeakReferenceMessenger.Default.Register<BackBreadcrumNavigationRequested>(this);
        WeakReferenceMessenger.Default.Register<AccountCreatedMessage>(this);
        WeakReferenceMessenger.Default.Register<WelcomeImportCompletedMessage>(this);
    }

    public Type? GetPageType(WinoPage page) => page switch
    {
        WinoPage.WelcomePageV2 or WinoPage.WelcomeHostPage => typeof(WelcomePageV2ViewController),
        WinoPage.ProviderSelectionPage => typeof(ProviderSelectionPageViewController),
        WinoPage.AccountSetupProgressPage => typeof(AccountSetupProgressPageViewController),
        WinoPage.AboutPage => typeof(AboutPageViewController),
        WinoPage.MailListPage => typeof(MailListPageViewController),
        _ => null
    };

    public bool Navigate(WinoPage page, object? parameter = null, NavigationReferenceFrame? frame = null, NavigationTransitionType transition = NavigationTransitionType.None)
    {
        if (_stopping || GetPageType(page) is null) return false;
        _pending = ObserveAsync(NavigateAsync(page, parameter));
        return true;
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (Exception exception) { _services.GetRequiredService<IWinoLogger>().CaptureException(exception, nameof(AppKitNavigationService)); }
    }

    public Task NavigateAsync(WinoPage page, object? parameter = null) => NavigateOwnedAsync(page, parameter, NavigationMode.New, addHistory: true);

    private async Task NavigateOwnedAsync(WinoPage page, object? parameter, NavigationMode mode, bool addHistory)
    {
        await _gate.WaitAsync();
        try
        {
            if (_stopping)
            {
                if (parameter is NavigateMailFolderEventArgs folder) folder.FolderInitLoadAwaitTask?.TrySetResult(false);
                return;
            }
            NSViewController controller = null!;
            await _dispatcher.ExecuteOnUIThread(() => controller = CreateController(page));
            if (addHistory && _current != null) _history.Push((_page, _parameter));
            _page = page; _parameter = parameter;
            if (_shell is not null && page is WinoPage.AboutPage or WinoPage.MailListPage)
            {
                await _shell.SetContentAsync(controller, parameter);
                return;
            }
            var previous = _current;
            _current = controller;
            await _dispatcher.ExecuteOnUIThread(() => _host(controller));
            if (previous is IWinoViewController old) await old.ReleaseAsync();
            await _dispatcher.ExecuteOnUIThread(() => previous?.Dispose());
            await ((IWinoViewController)controller).ActivateAsync(mode, parameter);
        }
        finally { _gate.Release(); }
    }

    public async Task ShowShellAsync()
    {
        WinoAppShellViewController? activatedShell = null;
        await _gate.WaitAsync();
        try
        {
            if (_stopping) return;
            if (_shell is not null) return;
            WinoAppShellViewController shell = null!;
            await _dispatcher.ExecuteOnUIThread(() => shell = _services.GetRequiredService<WinoAppShellViewController>());
            var previous = _current;
            _shell = shell; _current = shell; _page = WinoPage.None; _parameter = null;
            _history.Clear();
            await _dispatcher.ExecuteOnUIThread(() => _host(shell));
            if (previous is IWinoViewController old) await old.ReleaseAsync();
            await _dispatcher.ExecuteOnUIThread(() => previous?.Dispose());
            activatedShell = shell;
        }
        finally { _gate.Release(); }
        // Mail shell initialization awaits its first folder route. That route uses this
        // same router, so it must be allowed to enter before initialization can finish.
        if (activatedShell is not null) await activatedShell.ActivateAsync(NavigationMode.New, null);
    }

    public bool ChangeApplicationMode(WinoApplicationMode mode) => ChangeApplicationMode(mode, null!);
    private NSViewController CreateController(WinoPage page) => page switch
    {
        WinoPage.WelcomePageV2 or WinoPage.WelcomeHostPage => _services.GetRequiredService<WelcomePageV2ViewController>(),
        WinoPage.ProviderSelectionPage => _services.GetRequiredService<ProviderSelectionPageViewController>(),
        WinoPage.AccountSetupProgressPage => _services.GetRequiredService<AccountSetupProgressPageViewController>(),
        WinoPage.AboutPage => _services.GetRequiredService<AboutPageViewController>(),
        WinoPage.MailListPage => _services.GetRequiredService<MailListPageViewController>(),
        _ => throw new NotSupportedException($"The native route {page} has not been implemented.")
    };
    public bool ChangeApplicationMode(WinoApplicationMode mode, ShellModeActivationContext activationContext)
    {
        if (mode != WinoApplicationMode.Mail || _stopping) return false;
        _pending = ObserveAsync(ShowShellAsync());
        return true;
    }
    public bool ParkShell() => _shell is not null;
    public bool RestoreShell(WinoApplicationMode mode) => ChangeApplicationMode(mode);
    public bool RestoreShell(WinoApplicationMode mode, ShellModeActivationContext activationContext) => ChangeApplicationMode(mode, activationContext);
    public bool CanGoBack() => !_stopping && _history.Count > 0;
    public void GoBack(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight) => _pending = ObserveAsync(GoBackAsync(slideEffect));
    public async Task<bool> GoBackAsync(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight)
    {
        if (!CanGoBack()) return false;
        var route = _history.Pop();
        if (route.Page == WinoPage.None && _shell is not null)
        {
            await _shell.ClearContentAsync();
            _page = WinoPage.None; _parameter = null;
            return true;
        }
        await NavigateOwnedAsync(route.Page, route.Parameter, NavigationMode.Back, addHistory: false);
        return true;
    }
    public void SetNavigationResult(NavigationResult result) => _result = result;
    public void Receive(BreadcrumbNavigationRequested message) => Navigate(message.PageType, message.Parameter);
    public void Receive(BackBreadcrumNavigationRequested message) => GoBack();
    public void Receive(AccountCreatedMessage message) => _pending = ObserveAsync(ShowShellAsync());
    public void Receive(WelcomeImportCompletedMessage message) => _pending = ObserveAsync(ShowShellAsync());

    public async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        await _pending;
        await _gate.WaitAsync();
        try
        {
            if (_current is IWinoViewController controller) await controller.ReleaseAsync();
            await _dispatcher.ExecuteOnUIThread(() => _current?.Dispose());
            _current = null; _shell = null;
        }
        finally { _gate.Release(); }
    }
    public void Dispose() => WeakReferenceMessenger.Default.UnregisterAll(this);
}
