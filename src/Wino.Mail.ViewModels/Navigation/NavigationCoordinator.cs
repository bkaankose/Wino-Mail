#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Navigation;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels.Navigation;

/// <summary>
/// Platform-neutral routing for heads that host native pages (AppKit today, UIKit next). Owns the
/// back stacks, the navigation result, back confirmation (<see cref="IConfirmBackNavigation"/>,
/// <see cref="IBackNavigationAware"/>), the navigation messages and the account lifecycle reactions
/// (new account: shell and first synchronization; last account removed: Welcome). The head's
/// <see cref="INavigationHost"/> only creates, shows and releases pages.
/// </summary>
public sealed partial class NavigationCoordinator : INavigationService, IDisposable,
    IRecipient<BreadcrumbNavigationRequested>, IRecipient<BackBreadcrumNavigationRequested>, IRecipient<AccountCreatedMessage>,
    IRecipient<WelcomeImportCompletedMessage>, IRecipient<AccountRemovedMessage>
{
    private readonly INavigationHost _host;
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stack<(WinoPage Page, object? Parameter)> _history = new();
    // Onboarding pages opened while the shell is up (Settings > Add account) use a secondary surface
    // so the main window and its mail state stay in place.
    private readonly Stack<(WinoPage Page, object? Parameter)> _secondaryHistory = new();
    private (WinoPage Page, object? Parameter) _secondaryRoute;
    private WinoPage _page;
    private object? _parameter;
    private NavigationResult? _result;
    private bool _stopping;
    private Task _pending = Task.CompletedTask;
    // Completes once the current shell finished its first mode activation (its menus and first route).
    private Task _shellActivated = Task.CompletedTask;
    private readonly HashSet<WinoPage> _reportedMissingRoutes = new();

    /// <param name="services">Resolves the logger, account service, runtime and wizard context lazily, when a route needs them.</param>
    /// <param name="messenger">Defaults to <see cref="WeakReferenceMessenger.Default"/>; tests pass their own.</param>
    public NavigationCoordinator(INavigationHost host, IServiceProvider services, IDispatcher dispatcher, IMessenger? messenger = null)
    {
        _host = host;
        _services = services;
        _dispatcher = dispatcher;
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _messenger.Register<BreadcrumbNavigationRequested>(this);
        _messenger.Register<BackBreadcrumNavigationRequested>(this);
        _messenger.Register<AccountCreatedMessage>(this);
        _messenger.Register<WelcomeImportCompletedMessage>(this);
        _messenger.Register<AccountRemovedMessage>(this);
    }

    /// <summary>The page the main window or the shell content area shows; None for an empty shell.</summary>
    public WinoPage CurrentPage => _page;

    public object? CurrentParameter => _parameter;

    /// <summary>False while account setup is still working in the main window.</summary>
    public bool CanQuit => _page != WinoPage.AccountSetupProgressPage || !_host.MainContentHasPendingWork;

    /// <summary>True when the shell is shown and has finished its first mode activation.</summary>
    public bool IsShellReady => _host.IsShellShown && _shellActivated.IsCompleted;

    /// <summary>The last fire-and-forget navigation. Failures are logged, never thrown.</summary>
    public Task PendingNavigation => _pending;

    public Type? GetPageType(WinoPage winoPage) => _host.GetPageType(winoPage);

    public bool Navigate(WinoPage page, object? parameter = null, NavigationReferenceFrame? frame = null, NavigationTransitionType transition = NavigationTransitionType.None)
    {
        if (_stopping) return false;
        if (!_host.TryGetRoute(page, parameter, out var host))
        {
            bool firstMiss;
            lock (_reportedMissingRoutes) firstMiss = _reportedMissingRoutes.Add(page);
            if (firstMiss) Log.Warning("Navigation to {Page} was ignored: this head has no route for it.", page);
            return false;
        }
        _pending = host switch
        {
            NavigationHostKind.RenderingFrame => ObserveAsync(ShowInRenderingFrameAsync(page, parameter)),
            NavigationHostKind.Settings => ObserveAsync(_host.ShowSettingsAsync(page, parameter)),
            _ => ObserveAsync(NavigateAsync(page, parameter))
        };
        return true;
    }

    public Task NavigateAsync(WinoPage page, object? parameter = null)
    {
        if (_host.TryGetRoute(page, parameter, out var host))
        {
            if (host == NavigationHostKind.RenderingFrame) return ShowInRenderingFrameAsync(page, parameter);
            if (host == NavigationHostKind.Settings) return _host.ShowSettingsAsync(page, parameter);
        }
        return NavigateOwnedAsync(page, parameter, NavigationMode.New, addHistory: true);
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (Exception exception) { _services.GetService<IWinoLogger>()?.CaptureException(exception, nameof(NavigationCoordinator)); }
    }

    private Task ShowInRenderingFrameAsync(WinoPage page, object? parameter)
        => _stopping ? Task.CompletedTask : _host.ShowInRenderingFrameAsync(page, parameter);

    private async Task NavigateOwnedAsync(WinoPage page, object? parameter, NavigationMode mode, bool addHistory)
    {
        await _gate.WaitAsync();
        try
        {
            if (_stopping || !_host.TryGetRoute(page, parameter, out var host))
            {
                if (parameter is NavigateMailFolderEventArgs folder) folder.FolderInitLoadAwaitTask?.TrySetResult(false);
                return;
            }
            if (_host.IsShellShown && host == NavigationHostKind.ShellContent)
            {
                await _host.SetShellContentAsync(page, parameter);
                if (addHistory) _history.Push((_page, _parameter));
                _page = page; _parameter = parameter;
                return;
            }
            if (_host.IsShellShown && host == NavigationHostKind.Window)
            {
                await ShowInSecondaryWindowAsync(page, parameter, mode, addHistory);
                return;
            }
            await ReplaceMainContentAsync(page, parameter, mode, addHistory);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Hosts the page as the main window content. Call under the gate.</summary>
    private Task ReplaceMainContentAsync(WinoPage page, object? parameter, NavigationMode mode, bool addHistory)
    {
        var hadContent = _host.HasMainContent;
        return _host.ReplaceMainContentAsync(page, parameter, mode, () =>
        {
            if (addHistory && hadContent) _history.Push((_page, _parameter));
            _page = page; _parameter = parameter;
        });
    }

    /// <summary>Hosts the page on the secondary surface. Call under the gate.</summary>
    private Task ShowInSecondaryWindowAsync(WinoPage page, object? parameter, NavigationMode mode, bool addHistory)
    {
        var hadPage = _host.IsSecondaryWindowShown;
        return _host.ShowInSecondaryWindowAsync(page, parameter, mode, () =>
        {
            if (addHistory && hadPage) _secondaryHistory.Push(_secondaryRoute);
            _secondaryRoute = (page, parameter);
        });
    }

    private async Task CloseSecondaryWindowAsync(bool closedByUser = false)
    {
        if (!_host.IsSecondaryWindowShown) return;
        _secondaryHistory.Clear();
        await _host.CloseSecondaryWindowAsync(closedByUser);
    }

    /// <summary>The head calls this when the user closed the secondary surface directly.</summary>
    public void OnSecondaryWindowClosedByUser() => _pending = ObserveAsync(CloseSecondaryWindowAsync(closedByUser: true));

    public async Task ShowShellAsync()
    {
        Func<Task>? activate = null;
        TaskCompletionSource? activation = null;
        await _gate.WaitAsync();
        try
        {
            if (_stopping) return;
            if (_host.IsShellShown) return;
            activate = await _host.PresentShellAsync(() =>
            {
                _page = WinoPage.None; _parameter = null;
                _history.Clear();
                activation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _shellActivated = activation.Task;
            });
        }
        catch
        {
            activation?.TrySetResult();
            throw;
        }
        finally { _gate.Release(); }
        // Mail shell initialization awaits its first folder route. That route uses this
        // same router, so it must be allowed to enter before initialization can finish.
        if (activate is not null)
        {
            try { await activate(); }
            finally { activation?.TrySetResult(); }
        }
    }

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
        if (!_host.IsShellShown && _host.MainContentHasPendingWork) return false;
        await ShowShellAsync();
        await _shellActivated;
        if (_stopping || !_host.IsShellShown) return false;
        await _host.BringShellToFrontAsync();
        await _host.ActivateShellModeAsync(mode, parameter is null ? null : new ShellModeActivationContext { Parameter = parameter });
        return true;
    }

    public bool ChangeApplicationMode(WinoApplicationMode mode) => ChangeApplicationMode(mode, null);

    bool INavigationService.ChangeApplicationMode(WinoApplicationMode mode, ShellModeActivationContext activationContext)
        => ChangeApplicationMode(mode, activationContext);

    public bool ChangeApplicationMode(WinoApplicationMode mode, ShellModeActivationContext? activationContext)
    {
        if (_stopping) return false;
        if (mode == WinoApplicationMode.Settings)
        {
            if (!_host.IsSettingsAvailable) return false;
            _pending = ObserveAsync(_host.ShowSettingsAsync(null, null));
            return true;
        }
        _pending = ObserveAsync(ActivateModeAsync(mode, activationContext));
        return true;
    }

    /// <summary>Shows the shell if needed, then lets it activate the mode like the Windows WinoAppShell.</summary>
    private async Task ActivateModeAsync(WinoApplicationMode mode, ShellModeActivationContext? activationContext)
    {
        await ShowShellAsync();
        if (_host.IsShellShown && !_stopping) await _host.ActivateShellModeAsync(mode, activationContext);
    }

    /// <summary>True when the mode's root page has a route in the head (feature registries add them as they are built).</summary>
    public bool IsModeAvailable(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Mail => _host.TryGetRoute(WinoPage.MailListPage, null, out _),
        WinoApplicationMode.Calendar => _host.TryGetRoute(WinoPage.CalendarPage, null, out _),
        WinoApplicationMode.Contacts => _host.TryGetRoute(WinoPage.ContactsPage, null, out _),
        WinoApplicationMode.Tasks => _host.TryGetRoute(WinoPage.ToDoPage, null, out _),
        WinoApplicationMode.Settings => _host.IsSettingsAvailable,
        _ => false
    };

    public bool ParkShell() => _host.IsShellShown;
    public bool RestoreShell(WinoApplicationMode mode) => ChangeApplicationMode(mode);
    public bool RestoreShell(WinoApplicationMode mode, ShellModeActivationContext activationContext) => ChangeApplicationMode(mode, activationContext);
    public bool CanGoBack() => !_stopping && (_host.IsSecondaryWindowShown || _history.Count > 0);

    /// <summary>
    /// True when back leads to another page. False when the only step left would empty the shell (the first shell page),
    /// where a phone leaves the app instead of showing an empty mode.
    /// </summary>
    public bool CanGoBackToPage()
        => !_stopping && (_host.IsSecondaryWindowShown || (_history.TryPeek(out var previous) && (previous.Page != WinoPage.None || !_host.IsShellShown)));

    /// <summary>
    /// The page back would show in the main content (<see cref="WinoPage.None"/> for the empty shell), or null when
    /// there is no history or back closes a secondary window. Heads whose shell keeps one back stack per mode use it to
    /// keep back from crossing into another mode's page.
    /// </summary>
    public WinoPage? BackPage
        => _stopping || _host.IsSecondaryWindowShown || !_history.TryPeek(out var previous) ? null : previous.Page;

    public void GoBack(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight)
    {
        if (!_host.IsSecondaryWindowShown && _host.TrySettingsGoBack()) return;
        _pending = ObserveAsync(GoBackAsync(slideEffect));
    }

    public async Task<bool> GoBackAsync(NavigationTransitionEffect slideEffect = NavigationTransitionEffect.FromRight)
    {
        if (!_host.IsSecondaryWindowShown && _host.TrySettingsGoBack()) return true;
        if (_host.IsSecondaryWindowShown)
        {
            if (_secondaryHistory.Count == 0) { await CloseSecondaryWindowAsync(); return true; }
            var back = _secondaryHistory.Pop();
            await _gate.WaitAsync();
            try { await ShowInSecondaryWindowAsync(back.Page, back.Parameter, NavigationMode.Back, addHistory: false); }
            finally { _gate.Release(); }
            return true;
        }
        if (!CanGoBack()) return false;
        // Same contract as the Windows NavigationService: the page on screen may refuse to leave.
        if (_host.ShellContentViewModel is IConfirmBackNavigation confirm && !await confirm.CanNavigateBackAsync())
        {
            _result = null;
            return false;
        }
        var route = _history.Pop();
        if (route.Page == WinoPage.None && _host.IsShellShown)
        {
            await _host.ClearShellContentAsync();
            _page = WinoPage.None; _parameter = null;
            return true;
        }
        await NavigateOwnedAsync(route.Page, route.Parameter, NavigationMode.Back, addHistory: false);
        var result = _result;
        _result = null;
        if (_host.ShellContentViewModel is IBackNavigationAware aware)
            await _dispatcher.ExecuteOnUIThread(() => aware.OnNavigatedBack(route.Parameter, result));
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

    private async Task AccountCreatedAsync(MailAccount account)
    {
        // From Settings > Add account the shell already exists: just close the add-account surface.
        if (_host.IsSecondaryWindowShown) await CloseSecondaryWindowAsync();
        var shellExisted = _host.IsShellShown;
        await ShowShellAsync();
        // Windows App.Receive(AccountCreatedMessage): the runtime holds a new account's first full
        // mail, calendar, contacts and To Do synchronization until the host hands it over after the
        // shell is up. Without it only the shell's Inbox sync ran and calendar events never arrived.
        await _services.GetRequiredService<IApplicationRuntime>().SynchronizeCreatedAccountAsync(account);
        await ShowCreatedAccountAsync(account, shellExisted);
    }

    /// <summary>
    /// Windows WinoAppShell.Receive(AccountCreatedMessage): switch to the first mode the account provides and,
    /// for mail, refresh the menu and select the account.
    /// </summary>
    private async Task ShowCreatedAccountAsync(MailAccount account, bool shellExisted)
    {
        if (_stopping || !_host.IsShellShown) return;
        var mode = ShellModeSelection.ForCreatedAccount(account);
        // The mode switch runs first so the mail menu refresh below never navigates while another mode is on screen.
        // Activating the mode already on screen does nothing, so this does not navigate twice.
        await ActivateModeAsync(mode, null);
        // A shell presented just now built its menus from storage, new account included; refreshing them again would
        // only repeat the first folder route.
        if (!shellExisted || mode != WinoApplicationMode.Mail || _stopping) return;
        if (_services.GetService<IMailShellClient>() is { } mail) await mail.HandleAccountCreatedAsync(account);
    }

    public void Receive(WelcomeImportCompletedMessage message) => _pending = ObserveAsync(ShowShellAsync());

    public void Receive(AccountRemovedMessage message) => _pending = ObserveAsync(AccountRemovedAsync());

    /// <summary>
    /// Windows App.HandleAccountRemovedAsync: once the last account is gone while the shell is up
    /// (not during an onboarding rollback), every app window closes and the Welcome page returns.
    /// </summary>
    private async Task AccountRemovedAsync()
    {
        if (_stopping || !_host.IsShellShown) return;
        var accounts = await _services.GetRequiredService<IAccountService>().GetAccountsAsync();
        if (accounts.Count > 0 || _stopping || !_host.IsShellShown) return;
        _services.GetService<WelcomeWizardContext>()?.Reset();
        await ReturnToWelcomeAsync();
    }

    private async Task ReturnToWelcomeAsync()
    {
        if (_host.IsSettingsAvailable) await _host.CloseSettingsAsync();
        await CloseSecondaryWindowAsync();
        await _gate.WaitAsync();
        try
        {
            if (_stopping || !_host.IsShellShown) return;
            // The shell is released by the content switch; a new account creates a fresh shell.
            _history.Clear();
            await ReplaceMainContentAsync(WinoPage.WelcomePageV2, null, NavigationMode.New, addHistory: false);
            _history.Clear();
            await _host.DismissShellSurfacesAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        _messenger.UnregisterAll(this);
        await _pending;
        if (_host.IsSettingsAvailable) await _host.StopSettingsAsync();
        await CloseSecondaryWindowAsync();
        await _gate.WaitAsync();
        try { await _host.ReleaseMainContentAsync(); }
        finally { _gate.Release(); }
    }

    public void Dispose() => _messenger.UnregisterAll(this);
}
