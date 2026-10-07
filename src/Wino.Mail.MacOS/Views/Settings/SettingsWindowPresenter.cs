using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.UI;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Owns the Settings window and its in-window history. Every page in the window is a fresh
/// controller (transient ViewModels, as on Windows); back and forward recreate the page with its
/// original parameter. Swaps are serialized; the outgoing page is released after the new page is
/// on screen so a slow release never blocks the window.
/// </summary>
public sealed class SettingsWindowPresenter : ISettingsWindowPresenter, IRecipient<LanguageChanged>, IRecipient<WinoIntelligenceEntitlementChanged>
{
    private sealed record Entry(WinoPage Page, object? Parameter);

    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly IWinoLogger _logger;
    private readonly MacPageRegistry _registry;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Entry> _back = new();
    private readonly List<Entry> _forward = new();
    private SettingsWindowController? _window;
    private Entry? _current;
    private NSViewController? _currentController;
    private volatile bool _isActive;
    private volatile bool _isVisible;
    private volatile int _backCount;
    private bool _stopping;
    private bool? _lastCanAccessIntelligence;

    public SettingsWindowPresenter(IServiceProvider services, IDispatcher dispatcher, IWinoLogger logger, MacPageRegistry registry)
    {
        _services = services;
        _dispatcher = dispatcher;
        _logger = logger;
        _registry = registry;
        SettingsPageCatalog.IsSmimeAvailable = services.GetService<IPlatformCapabilities>()?.Smime ?? false;
        var entitlement = services.GetService<IWinoAccountIntelligenceSnapshotService>();
        SettingsPageCatalog.CanAccessIntelligence = () => entitlement?.CurrentEntitlement?.CanAccessSurfaces ?? false;
        _lastCanAccessIntelligence = entitlement?.CurrentEntitlement?.CanAccessSurfaces ?? false;
        WeakReferenceMessenger.Default.Register<LanguageChanged>(this);
        WeakReferenceMessenger.Default.Register<WinoIntelligenceEntitlementChanged>(this);
    }

    public async Task ShowAsync(WinoPage? page = null, object? parameter = null)
    {
        if (_stopping) return;
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            EnsureWindow().Present();
            _isVisible = true;
        });

        if (page is null || page is WinoPage.SettingOptionsPage or WinoPage.SettingsPage)
        {
            if (_current is null) await NavigateAsync(new Entry(SettingsPageCatalog.DefaultPage, null), NavigationMode.New);
            return;
        }

        var target = new Entry(page.Value, parameter);
        if (_current is { } current && current.Page == target.Page && Equals(current.Parameter, target.Parameter)) return;
        await NavigateAsync(target, NavigationMode.New);
    }

    public bool TryGoBack()
    {
        if (_stopping || !_isVisible || !_isActive || _backCount == 0) return false;
        _ = ObserveAsync(GoBackAsync());
        return true;
    }

    public Task GoBackAsync() => NavigateAsync(null, NavigationMode.Back);

    public Task GoForwardAsync() => NavigateAsync(null, NavigationMode.Forward);

    public async Task CloseAsync()
    {
        if (_stopping) return;
        await _dispatcher.ExecuteOnUIThread(() =>
        {
            _isVisible = false;
            _isActive = false;
            _window?.Close();
        });
        // Closing raises Closing, which clears too; clearing here makes the reset complete before returning.
        await ClearAsync();
    }

    public async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        await _gate.WaitAsync();
        try
        {
            await ReleaseAsync(_currentController);
            _currentController = null;
            _current = null;
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                _window?.ClearPage();
                _window?.Close();
            });
        }
        finally { _gate.Release(); }
    }

    public void Receive(LanguageChanged message)
    {
        if (_stopping || _window is null) return;
        _ = ObserveAsync(ReloadAsync());
    }

    /// <summary>
    /// Mirrors SettingsMenuProvider: rebuild the sidebar when the entitlement changes, and leave the
    /// Wino Intelligence pages for Wino Account when access is lost.
    /// </summary>
    public void Receive(WinoIntelligenceEntitlementChanged message)
    {
        if (_stopping || _window is null) return;
        // Every snapshot refresh republishes the entitlement. Only an access change affects the
        // sidebar; recreating the current page here restarted the Intelligence page's own refresh,
        // which published again and reloaded the page in an endless loop.
        bool canAccess = message.Entitlement.CanAccessSurfaces;
        if (_lastCanAccessIntelligence == canAccess) return;
        _lastCanAccessIntelligence = canAccess;
        _ = ObserveAsync(ReloadForEntitlementAsync(canAccess));
    }

    private async Task ReloadForEntitlementAsync(bool canAccess)
    {
        await _dispatcher.ExecuteOnUIThread(() => _window?.Sidebar.Reload());
        if (!canAccess && _current is { } current && SettingsPageCatalog.RootPage(current.Page) == WinoPage.WinoIntelligencePage)
            await NavigateAsync(new Entry(WinoPage.WinoAccountManagementPage, null), NavigationMode.New);
    }

    private async Task ReloadAsync()
    {
        await _dispatcher.ExecuteOnUIThread(() => _window?.Sidebar.Reload());
        if (_current is not null) await NavigateAsync(_current, NavigationMode.Refresh);
    }

    private SettingsWindowController EnsureWindow()
    {
        if (_window is not null) return _window;
        var window = new SettingsWindowController();
        window.BackRequested += (_, _) => _ = ObserveAsync(GoBackAsync());
        window.ForwardRequested += (_, _) => _ = ObserveAsync(GoForwardAsync());
        window.SidebarPageSelected += (_, page) =>
        {
            if (_current?.Page == page) return;
            _ = ObserveAsync(NavigateAsync(new Entry(page, null), NavigationMode.New));
        };
        window.ActiveChanged += (_, active) => _isActive = active;
        window.Closing += (_, _) =>
        {
            _isVisible = false;
            _isActive = false;
            _ = ObserveAsync(ClearAsync());
        };
        _window = window;
        return window;
    }

    /// <summary>Closing the window releases its page and history; reopening starts at the default page.</summary>
    private async Task ClearAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var previous = _currentController;
            _currentController = null;
            _current = null;
            _back.Clear();
            _forward.Clear();
            _backCount = 0;
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                _window?.ClearPage();
                _window?.SetNavigationState(false, false);
            });
            await ReleaseAsync(previous);
        }
        finally { _gate.Release(); }
    }

    /// <param name="requested">The page for <see cref="NavigationMode.New"/> and <see cref="NavigationMode.Refresh"/>; history supplies it otherwise.</param>
    private async Task NavigateAsync(Entry? requested, NavigationMode mode)
    {
        if (_stopping) return;
        IWinoViewController? activate = null;
        Entry? target;
        await _gate.WaitAsync();
        try
        {
            if (_stopping || _window is null) return;
            target = mode switch
            {
                NavigationMode.Back => _back.Count > 0 ? _back[^1] : null,
                NavigationMode.Forward => _forward.Count > 0 ? _forward[^1] : null,
                _ => requested
            };
            if (target is null) return;

            NSViewController controller = null!;
            string title = string.Empty;
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                controller = CreateController(target.Page);
                title = ResolveTitle(controller, target.Page);
                _ = controller.View;
            });

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

            var previous = _currentController;
            _current = target;
            _currentController = controller;
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                Detach(previous);
                _window.SetPage(controller, title, target.Page, SettingsPageCatalog.RootPage(target.Page));
                Attach(controller);
                _window.SetNavigationState(_back.Count > 0, _forward.Count > 0);
            });
            _ = ObserveAsync(ReleaseAsync(previous));
            activate = controller as IWinoViewController;
        }
        finally { _gate.Release(); }

        if (activate is not null)
        {
            try { await activate.ActivateAsync(mode == NavigationMode.Refresh ? NavigationMode.Refresh : mode, target.Parameter); }
            catch (ObjectDisposedException) { }
            catch (Exception exception) { _logger.CaptureException(exception, nameof(SettingsWindowPresenter)); }
        }
    }

    private NSViewController CreateController(WinoPage page)
    {
        if (!_registry.TryGet(page, out var type, out var host) || host != MacPageHost.SettingsWindow || type == typeof(SettingsPlaceholderViewController))
            return new SettingsPlaceholderViewController(page);
        try
        {
            return (NSViewController)_services.GetRequiredService(type);
        }
        catch (Exception exception)
        {
            _logger.CaptureException(exception, nameof(SettingsWindowPresenter));
            return new SettingsPlaceholderViewController(page, SettingsPlaceholderViewController.FailedMessage);
        }
    }

    private static string ResolveTitle(NSViewController controller, WinoPage page)
        => controller is ISettingsPageTitleSource { PageTitle: { Length: > 0 } title } ? title : SettingsPageCatalog.Title(page);

    private void Attach(NSViewController controller)
    {
        if (controller is ISettingsPageTitleSource source) source.PageTitleChanged += PageTitleChanged;
    }

    private void Detach(NSViewController? controller)
    {
        if (controller is ISettingsPageTitleSource source) source.PageTitleChanged -= PageTitleChanged;
    }

    private void PageTitleChanged(object? sender, EventArgs args)
    {
        if (sender is not NSViewController controller || !ReferenceEquals(controller, _currentController) || _current is null) return;
        var page = _current.Page;
        _ = _dispatcher.ExecuteOnUIThread(() => _window?.SetTitle(ResolveTitle(controller, page), page, SettingsPageCatalog.RootPage(page)));
    }

    private async Task ReleaseAsync(NSViewController? controller)
    {
        if (controller is null) return;
        await _dispatcher.ExecuteOnUIThread(() => Detach(controller));
        try
        {
            if (controller is IWinoViewController page) await page.ReleaseAsync();
        }
        catch (Exception exception) { _logger.CaptureException(exception, nameof(SettingsWindowPresenter)); }
        finally { await _dispatcher.ExecuteOnUIThread(controller.Dispose); }
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (Exception exception) { _logger.CaptureException(exception, nameof(SettingsWindowPresenter)); }
    }
}
