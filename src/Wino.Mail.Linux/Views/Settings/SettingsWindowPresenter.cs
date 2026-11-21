using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Navigation;
using Wino.Core.Domain.Messaging.Client.Shell;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Owns the Settings window and its in-window history, as the macOS presenter does. Every page in the window is a
/// fresh view (transient ViewModels, as on Windows); back and forward recreate the page with its original parameter.
/// Swaps are serialized; the outgoing page is released after the new page is on screen.
/// </summary>
internal sealed class SettingsWindowPresenter : ISettingsWindowPresenter
{
    private sealed record Entry(WinoPage Page, object? Parameter);

    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly IWinoLogger _logger;
    private readonly LinuxPageRegistry _registry;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Entry> _back = [];
    private readonly List<Entry> _forward = [];
    private SettingsWindow? _window;
    private Entry? _current;
    private IWinoView? _currentView;
    private volatile bool _isActive;
    private volatile bool _isVisible;
    private volatile int _backCount;
    private volatile bool _stopping;
    private bool _closeConfirmed;
    private bool? _lastCanAccessIntelligence;

    public SettingsWindowPresenter(IServiceProvider services, IDispatcher dispatcher, IWinoLogger logger, LinuxPageRegistry registry, IMessenger messenger)
    {
        _services = services;
        _dispatcher = dispatcher;
        _logger = logger;
        _registry = registry;
        _messenger = messenger;
        _lastCanAccessIntelligence = CanAccessIntelligence();
        messenger.Register<SettingsWindowPresenter, LanguageChanged>(this, static (recipient, _) => recipient.OnLanguageChanged());
        messenger.Register<SettingsWindowPresenter, WinoIntelligenceEntitlementChanged>(this, static (recipient, message) => recipient.OnEntitlementChanged(message));
        messenger.Register<SettingsWindowPresenter, SettingsRootNavigationRequested>(this, static (recipient, message) => recipient.OnRootNavigationRequested(message));
    }

    public WinoPage? CurrentPage => _current?.Page;
    public object? CurrentParameter => _current?.Parameter;
    public int BackDepth => _backCount;
    public int ForwardDepth => _forward.Count;
    public SettingsWindow? Window => _window;
    public IWinoView? CurrentView => _currentView;
    public bool IsVisible => _isVisible;
    public bool IsActive => _isActive;

    private GtkDialogService? Dialogs => _services.GetService<GtkDialogService>();

    public async Task ShowAsync(WinoPage? page, object? parameter)
    {
        if (_stopping) return;
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            EnsureWindow().Present();
            _isVisible = true;
        });

        // The settings root with a page parameter is a deep link (the reader's Manage sender rules).
        if (page is WinoPage.SettingOptionsPage or WinoPage.SettingsPage && parameter is WinoPage deepLink
            && deepLink is not (WinoPage.SettingOptionsPage or WinoPage.SettingsPage))
        {
            page = deepLink;
            parameter = null;
        }

        if (page is null || page is WinoPage.SettingOptionsPage or WinoPage.SettingsPage)
        {
            if (_current is null) await NavigateAsync(new Entry(SettingsCatalog.DefaultPage, null), NavigationMode.New, reveal: false);
            return;
        }

        var target = new Entry(page.Value, parameter);
        if (_current is { } current && current.Page == target.Page)
        {
            // An open page that accepts the parameter handles it in place (the checkout return on Wino Account).
            if (parameter is not null && _currentView is ISettingsPageParameterReceiver receiver)
            {
                try
                {
                    if (await receiver.ReceiveParameterAsync(parameter)) return;
                }
                catch (Exception exception) { Report(exception); }
            }

            if (Equals(current.Parameter, target.Parameter)) return;
        }
        await NavigateAsync(target, NavigationMode.New);
    }

    public bool TryGoBack()
    {
        if (_stopping || !_isVisible || !_isActive || _backCount == 0) return false;
        // Page-initiated back (after Save, Delete or Cancel): the page already decided, so it is not asked again.
        _ = ObserveAsync(NavigateAsync(null, NavigationMode.Back));
        return true;
    }

    public async Task GoBackAsync()
    {
        if (_backCount > 0 && await ConfirmLeaveAsync()) await NavigateAsync(null, NavigationMode.Back);
    }

    public async Task GoForwardAsync()
    {
        if (_forward.Count > 0 && await ConfirmLeaveAsync()) await NavigateAsync(null, NavigationMode.Forward);
    }

    public async Task CloseAsync()
    {
        if (_stopping) return;
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            _window?.Hide();
            OnWindowHidden();
        });
        await ClearAsync();
    }

    public async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        _messenger.UnregisterAll(this);
        await _gate.WaitAsync();
        try
        {
            var previous = _currentView;
            _currentView = null;
            _current = null;
            await ReleaseAsync(previous);
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                _isVisible = false;
                _isActive = false;
                if (Dialogs is { } dialogs && ReferenceEquals(dialogs.Surface, _window)) dialogs.SetSurface(null);
                _window?.Destroy();
            });
        }
        finally { _gate.Release(); }
    }

    private void OnRootNavigationRequested(SettingsRootNavigationRequested message)
    {
        if (_stopping || _window is null) return;
        _ = ObserveAsync(ShowAsync(message.PageType, null));
    }

    private void OnLanguageChanged()
    {
        if (_stopping || _window is null) return;
        _ = ObserveAsync(ReloadAsync());
    }

    // Every snapshot refresh republishes the entitlement; only an access change affects the sidebar. Reloading the
    // current page on every message restarts the Intelligence page's own refresh, which publishes again (a loop).
    private void OnEntitlementChanged(WinoIntelligenceEntitlementChanged message)
    {
        if (_stopping || _window is null) return;
        var canAccess = message.Entitlement.CanAccessSurfaces;
        if (_lastCanAccessIntelligence == canAccess) return;
        _lastCanAccessIntelligence = canAccess;
        _ = ObserveAsync(ReloadForEntitlementAsync(canAccess));
    }

    private async Task ReloadForEntitlementAsync(bool canAccess)
    {
        await _dispatcher.ExecuteOnUIThread(() => _window?.Sidebar.Reload());
        if (!canAccess && _current is { } current && IsIntelligenceSubPage(current.Page))
            await NavigateAsync(new Entry(WinoPage.WinoIntelligencePage, null), NavigationMode.New);
    }

    private static bool IsIntelligenceSubPage(WinoPage page)
        => page is WinoPage.WinoIntelligenceManagementPage or WinoPage.IntelligenceCoveragePage or WinoPage.MailClassificationPage;

    private bool CanAccessIntelligence()
        => _services.GetService<IWinoAccountIntelligenceSnapshotService>()?.CurrentEntitlement?.CanAccessSurfaces ?? false;

    private async Task ReloadAsync()
    {
        await _dispatcher.ExecuteOnUIThread(() => _window?.Sidebar.Reload());
        if (_current is { } current) await NavigateAsync(current, NavigationMode.Refresh);
    }

    public static WinoPage RootPage(WinoPage page) => page switch
    {
        WinoPage.SettingOptionsPage or WinoPage.SettingsPage => SettingsCatalog.DefaultPage,
        _ => SettingsCatalog.RootPage(page)
    };

    private IConfirmBackNavigation? CurrentConfirmation() => _currentView?.AssociatedViewModel as IConfirmBackNavigation;

    private async Task<bool> ConfirmLeaveAsync()
    {
        IConfirmBackNavigation? confirm = null;
        await _dispatcher.ExecuteOnUIThread(() => confirm = CurrentConfirmation());
        return confirm is null || await confirm.CanNavigateBackAsync();
    }

    private async Task LeaveToSidebarPageAsync(WinoPage page)
    {
        if (await ConfirmLeaveAsync())
        {
            await NavigateAsync(new Entry(page, null), NavigationMode.New);
            return;
        }
        // Refused: the sidebar goes back to the page that stays.
        if (_current is { } current) await _dispatcher.ExecuteOnUIThread(() => _window?.Sidebar.Select(RootPage(current.Page)));
    }

    // The breadcrumb's root button: the intermediate pages are dropped without being created again.
    private async Task GoToRootAsync(WinoPage root)
    {
        if (_current is not { } current || current.Page == root || !await ConfirmLeaveAsync()) return;
        var found = false;
        await _gate.WaitAsync();
        try
        {
            var index = _back.FindLastIndex(entry => entry.Page == root);
            if (index >= 0)
            {
                _back.RemoveRange(index + 1, _back.Count - index - 1);
                _backCount = _back.Count;
                found = true;
            }
        }
        finally { _gate.Release(); }

        if (found) await NavigateAsync(null, NavigationMode.Back);
        else await NavigateAsync(new Entry(root, null), NavigationMode.New);
    }

    private async Task ConfirmCloseAsync(IConfirmBackNavigation confirm)
    {
        if (!await confirm.CanNavigateBackAsync()) return;
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            _closeConfirmed = true;
            try { _window?.Window.Close(); }
            finally { _closeConfirmed = false; }
        });
    }

    private SettingsWindow EnsureWindow()
    {
        if (_window is not null) return _window;
        var window = new SettingsWindow(_services.GetRequiredService<ISettingsCatalog>());
        window.BackRequested += () => _ = ObserveAsync(GoBackAsync());
        window.ForwardRequested += () => _ = ObserveAsync(GoForwardAsync());
        window.RootRequested += root => _ = ObserveAsync(GoToRootAsync(root));
        window.Sidebar.PageSelected += page =>
        {
            if (_current?.Page == page) return;
            _ = ObserveAsync(LeaveToSidebarPageAsync(page));
        };
        window.Sidebar.SelectedPageActivated += page =>
        {
            window.RevealContent();
            if (_current is { } current && current.Page != page) _ = ObserveAsync(GoToRootAsync(page));
        };
        // A page with unsaved work (the theme editor) is asked before the window closes, then closed.
        window.ShouldClose = () =>
        {
            if (_closeConfirmed || CurrentConfirmation() is not { } confirm) return true;
            _ = ObserveAsync(ConfirmCloseAsync(confirm));
            return false;
        };
        window.ActiveChanged += OnActiveChanged;
        window.Closed += () =>
        {
            OnWindowHidden();
            _ = ObserveAsync(ClearAsync());
        };
        _window = window;
        return window;
    }

    // The window that becomes active is told after this one stops being active, hence the posted check. When the user
    // switched to another application the surface stays Settings, where a late result message belongs.
    private void OnActiveChanged(bool active)
    {
        _isActive = active;
        if (Dialogs is not { } dialogs || _window is not { } window) return;
        if (active)
        {
            dialogs.SetSurface(window);
            return;
        }
        MainLoop.Post(() =>
        {
            if (_services.GetService<GtkNavigationService>()?.MainWindow?.Window.IsActive == true && ReferenceEquals(dialogs.Surface, window))
                dialogs.SetSurface(null);
        });
    }

    private void OnWindowHidden()
    {
        _isVisible = false;
        _isActive = false;
        if (Dialogs is { } dialogs && _window is { } window && ReferenceEquals(dialogs.Surface, window)) dialogs.SetSurface(null);
    }

    /// <summary>Closing the window releases its page and history; reopening starts at the default page.</summary>
    private async Task ClearAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var previous = _currentView;
            _currentView = null;
            _current = null;
            _back.Clear();
            _forward.Clear();
            _backCount = 0;
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                _window?.ClearPage();
                _window?.SetNavigationState(false, false);
                _window?.Sidebar.Select(null);
            });
            await ReleaseAsync(previous);
        }
        finally { _gate.Release(); }
    }

    /// <param name="requested">The page for <see cref="NavigationMode.New"/> and <see cref="NavigationMode.Refresh"/>; history supplies it otherwise.</param>
    private async Task NavigateAsync(Entry? requested, NavigationMode mode, bool reveal = true)
    {
        if (_stopping) return;
        IWinoView? activate = null;
        Entry? target;
        await _gate.WaitAsync();
        try
        {
            if (_stopping || _window is not { } window) return;
            target = mode switch
            {
                NavigationMode.Back => _back.Count > 0 ? _back[^1] : null,
                NavigationMode.Forward => _forward.Count > 0 ? _forward[^1] : null,
                _ => requested
            };
            if (target is null) return;
            // The Wino Intelligence page is always listed and explains the subscription; only its sub-pages need access.
            if (mode == NavigationMode.New && IsIntelligenceSubPage(target.Page) && !CanAccessIntelligence())
                target = new Entry(WinoPage.WinoIntelligencePage, null);

            IWinoView? view = null;
            await _dispatcher.ExecuteOnUIThread(() => view = CreateView(target.Page, target.Parameter));
            if (view is null) return;

            switch (mode)
            {
                case NavigationMode.Back:
                    _back.RemoveAt(_back.Count - 1);
                    if (_current is not null) _forward.Add(_current);
                    break;
                case NavigationMode.Forward:
                    _forward.RemoveAt(_forward.Count - 1);
                    if (_current is not null) _back.Add(_current);
                    break;
                case NavigationMode.Refresh:
                    break;
                default:
                    if (_current is not null) _back.Add(_current);
                    _forward.Clear();
                    break;
            }
            _backCount = _back.Count;

            var previous = _currentView;
            _current = target;
            _currentView = view;
            var page = target.Page;
            var root = RootPage(page);
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                Detach(previous);
                window.SetPage(view.Root, ResolveTitle(view, page), page, root, ResolveDescription(view, page), reveal);
                Attach(view);
                window.Sidebar.Select(root);
                window.SetNavigationState(_back.Count > 0, _forward.Count > 0);
            });
            _ = ObserveAsync(ReleaseAsync(previous));
            activate = view;
        }
        finally { _gate.Release(); }

        if (activate is not null)
        {
            try { await activate.ActivateAsync(mode, target.Parameter); }
            catch (ObjectDisposedException) { }
            catch (Exception exception) { Report(exception); }
        }
    }

    private IWinoView CreateView(WinoPage page, object? parameter)
    {
        if (!_registry.TryGet(page, parameter, out var route) || route.Host != LinuxPageHost.Settings || route.ViewType == typeof(SettingsPlaceholderView))
            return new SettingsPlaceholderView(page);
        try
        {
            var view = route.Create(_services);
            _ = view.Root;
            return view;
        }
        catch (Exception exception)
        {
            Report(exception);
            return new SettingsPlaceholderView(page, Translator.MacOS_Placeholder_PageFailed);
        }
    }

    private string ResolveDescription(IWinoView view, WinoPage page)
        => (view as ISettingsPageTitleSource)?.PageDescription is { Length: > 0 } description
            ? description
            : _services.GetService<ISettingsCatalog>()?.BreadcrumbDescription(page) ?? string.Empty;

    private static string ResolveTitle(IWinoView view, WinoPage page)
        => view is ISettingsPageTitleSource { PageTitle: { Length: > 0 } title } ? title : SettingsCatalog.Title(page);

    private void Attach(IWinoView view)
    {
        if (view is ISettingsPageTitleSource source) source.PageTitleChanged += PageTitleChanged;
    }

    private void Detach(IWinoView? view)
    {
        if (view is ISettingsPageTitleSource source) source.PageTitleChanged -= PageTitleChanged;
    }

    private void PageTitleChanged(object? sender, EventArgs args)
        => _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            if (sender is not IWinoView view || !ReferenceEquals(view, _currentView) || _current is not { } current || _window is not { } window) return;
            var page = current.Page;
            window.SetHeader(ResolveTitle(view, page), page, RootPage(page), ResolveDescription(view, page));
        });

    private async Task ReleaseAsync(IWinoView? view)
    {
        if (view is null) return;
        await _dispatcher.ExecuteOnUIThread(() => Detach(view));
        try { await view.ReleaseAsync(); }
        catch (Exception exception) { Report(exception); }
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (Exception exception) { Report(exception); }
    }

    // CaptureException sends nothing while telemetry is off, which it always is on Linux.
    private void Report(Exception exception)
    {
        Serilog.Log.Error(exception, "The Settings window failed.");
        _logger.CaptureException(exception, nameof(SettingsWindowPresenter));
    }
}
