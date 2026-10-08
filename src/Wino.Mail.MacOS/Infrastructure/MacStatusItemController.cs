using AppKit;
using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Views.Companion;
using Wino.Mail.ViewModels.Companion;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// The menu bar icon, the macOS counterpart of the Windows tray icon (NativeTrayIcon and
/// MainTrayController). A left click toggles the companion popover under the icon; a right click
/// or Control-click opens the same menu as the Windows tray (Open Wino Mail, Open Wino Calendar,
/// Exit). While the companion is turned off a left click opens the menu too.
/// The companion surface is created on first use and dropped when it is disabled or fails, like
/// the Windows CompanionService (a failure disables it for the session and keeps the icon).
/// </summary>
public sealed class MacStatusItemController : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly IWinoLogger _logger;
    private readonly DateTimeOffset _sessionStartedAtUtc;
    private readonly CompanionNavigationCallbacks _navigation;
    private readonly Func<Task> _showWino;
    private readonly Func<Task> _showCalendar;
    private readonly Action _exit;
    private NSStatusItem? _item;
    private NSPopover? _popover;
    private PopoverDelegate? _popoverDelegate;
    private CompanionPopoverViewController? _content;
    private CompanionDashboardViewModel? _viewModel;
    private CompanionReadinessState _readiness = CompanionReadinessState.Initializing;
    private bool _companionEnabled = true;
    private bool _sessionAvailable = true;
    private bool _opening;
    private DateTime _closedAtUtc = DateTime.MinValue;
    private bool _disposed;

    public MacStatusItemController(IServiceProvider services, IDispatcher dispatcher, DateTimeOffset sessionStartedAtUtc,
        CompanionNavigationCallbacks navigation, Func<Task> showWino, Func<Task> showCalendar, Action exit)
    {
        _services = services;
        _dispatcher = dispatcher;
        _logger = services.GetRequiredService<IWinoLogger>();
        _sessionStartedAtUtc = sessionStartedAtUtc;
        _navigation = navigation;
        _showWino = showWino;
        _showCalendar = showCalendar;
        _exit = exit;
    }

    /// <summary>True while the icon is in the menu bar.</summary>
    public bool IsVisible => _item is not null;

    /// <summary>True while the companion popover is shown.</summary>
    public bool IsCompanionShown => _popover?.Shown == true;

    public bool IsCompanionAvailable => _companionEnabled && _sessionAvailable && !_disposed;

    public CompanionReadinessState Readiness => _readiness;

    /// <summary>Puts the icon in the menu bar. Returns false when AppKit could not create it.</summary>
    public bool Show()
    {
        if (_disposed) return false;
        if (_item is not null) return true;
        try
        {
            var item = NSStatusBar.SystemStatusBar.CreateStatusItem(NSStatusItemLength.Square);
            if (item.Button is not { } button)
            {
                NSStatusBar.SystemStatusBar.RemoveStatusItem(item);
                return false;
            }
            var image = WinoIcons.Image(WinoIconGlyph.Mail, 18, accessibilityDescription: "Wino Mail");
            image.Template = true;
            button.Image = image;
            button.ToolTip = "Wino Mail";
            WinoAccessibility.Label(button, "Wino Mail");
            button.SendActionOn((NSEventType)(ulong)(NSEventMask.LeftMouseUp | NSEventMask.RightMouseUp));
            button.Activated += ButtonActivated;
            // Not removable by Command-drag: in the background it is the way back into Wino.
            item.AutosaveName = "WinoMailStatusItem";
            _item = item;
            return true;
        }
        catch (Exception exception)
        {
            _logger.CaptureException(exception, "Creating the menu bar icon failed.");
            Hide();
            return false;
        }
    }

    /// <summary>Removes the icon and closes the companion.</summary>
    public void Hide()
    {
        CloseCompanion();
        if (_item is null) return;
        var item = _item;
        _item = null;
        if (item.Button is { } button) button.Activated -= ButtonActivated;
        NSStatusBar.SystemStatusBar.RemoveStatusItem(item);
        item.Dispose();
    }

    public void SetCompanionEnabled(bool enabled)
    {
        _companionEnabled = enabled;
        if (!enabled) DisposeSurface();
    }

    public void SetReadiness(CompanionReadinessState state)
    {
        if (_readiness == state) return;
        _readiness = state;
        // An open companion reloads for the new state (an account was added or removed).
        if (_viewModel is { IsOpen: true } viewModel) Observe(viewModel.OpenAsync(state, CancellationToken.None));
    }

    /// <summary>Opens the companion under the icon, or closes it when it is shown.</summary>
    public async Task ToggleCompanionAsync()
    {
        if (!IsCompanionAvailable || _item?.Button is not { } button || _opening) return;
        if (IsCompanionShown)
        {
            CloseCompanion();
            return;
        }
        // A transient popover already closes on the mouse-down of the click that brings us here.
        if (DateTime.UtcNow - _closedAtUtc < TimeSpan.FromMilliseconds(300)) return;

        _opening = true;
        try
        {
            EnsureSurface();
            await _viewModel!.OpenAsync(_readiness, CancellationToken.None);
            if (_item?.Button is null || _popover is null) return;
            // The popover takes keyboard focus (snooze menu, Escape) only while Wino is active.
            NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
            _content!.RefreshNow();
            _popover.ContentSize = _content.PreferredContentSize;
            _popover.Show(button.Bounds, button, NSRectEdge.MinYEdge);
            button.Highlight(true);
        }
        catch (Exception exception)
        {
            DisableForSession(exception, "Opening the companion popover failed.");
        }
        finally
        {
            _opening = false;
        }
    }

    public void CloseCompanion()
    {
        try
        {
            _popover?.Close();
            _viewModel?.Close();
        }
        catch (Exception exception)
        {
            DisableForSession(exception, "Closing the companion popover failed.");
        }
    }

    /// <summary>One line describing the icon and companion, for the debug bridge.</summary>
    public string Describe()
        => $"statusitem={(IsVisible ? "visible" : "hidden")} companion={(IsCompanionShown ? "shown" : "closed")} enabled={_companionEnabled} " +
           $"available={_sessionAvailable} readiness={_readiness} state={_viewModel?.SurfaceState.ToString() ?? "none"} " +
           $"size={(_content is null ? "-" : $"{_content.PreferredContentSize.Width}x{_content.PreferredContentSize.Height}")}";

    /// <summary>The menu the right click shows; also used by the debug bridge to list its items.</summary>
    public NSMenu BuildMenu()
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        menu.AddItem(new NSMenuItem(Translator.SystemTrayMenu_ShowWino, (_, _) => Observe(_showWino())));
        menu.AddItem(new NSMenuItem(Translator.SystemTrayMenu_ShowWinoCalendar, (_, _) => Observe(_showCalendar())));
        menu.AddItem(NSMenuItem.SeparatorItem);
        menu.AddItem(new NSMenuItem(Translator.SystemTrayMenu_ExitWino, (_, _) => _exit()));
        return menu;
    }

    private void ButtonActivated(object? sender, EventArgs args)
    {
        var current = NSApplication.SharedApplication.CurrentEvent;
        var secondary = current is not null && (current.Type == NSEventType.RightMouseUp
            || current.ModifierFlags.HasFlag(NSEventModifierMask.ControlKeyMask));
        if (secondary || !IsCompanionAvailable)
        {
            ShowMenu();
            return;
        }
        Observe(ToggleCompanionAsync());
    }

    private void ShowMenu()
    {
        if (_item is not { } item) return;
        CloseCompanion();
        // Assigning the menu for one click gives the native status item menu placement and highlight.
        item.Menu = BuildMenu();
        item.Button?.PerformClick(null);
        item.Menu = null;
    }

    private void EnsureSurface()
    {
        if (_viewModel is not null) return;
        var actions = new CompanionActionHandler(_services, _navigation);
        _viewModel = new CompanionDashboardViewModel(_services, actions, _dispatcher, _sessionStartedAtUtc);
        _viewModel.NavigationCompleted += SurfaceNavigationCompleted;
        _content = new CompanionPopoverViewController(_viewModel, _dispatcher, _logger, _services.GetRequiredService<IPictureStorageService>());
        _ = _content.View;
        _content.PreferredSizeChanged += ContentSizeChanged;
        _content.CloseRequested += ContentCloseRequested;
        _popoverDelegate = new PopoverDelegate(PopoverClosed);
        _popover = new NSPopover
        {
            ContentViewController = _content,
            Behavior = NSPopoverBehavior.Transient,
            Animates = true,
            Delegate = _popoverDelegate
        };
    }

    private void ContentSizeChanged(object? sender, EventArgs args)
    {
        if (_popover is { } popover && _content is { } content) popover.ContentSize = content.PreferredContentSize;
    }

    private void ContentCloseRequested(object? sender, EventArgs args) => CloseCompanion();

    private void SurfaceNavigationCompleted(object? sender, EventArgs args)
        => _ = _dispatcher.ExecuteOnUIThread(CloseCompanion);

    private void PopoverClosed()
    {
        _closedAtUtc = DateTime.UtcNow;
        _item?.Button?.Highlight(false);
        _viewModel?.Close();
    }

    private void DisposeSurface()
    {
        if (_viewModel is not null) _viewModel.NavigationCompleted -= SurfaceNavigationCompleted;
        if (_content is not null)
        {
            _content.PreferredSizeChanged -= ContentSizeChanged;
            _content.CloseRequested -= ContentCloseRequested;
        }
        try { _popover?.Close(); }
        catch (Exception exception) { _logger.CaptureException(exception, "Companion popover cleanup"); }
        _viewModel?.Close();
        _content?.Release();
        if (_popover is not null) { _popover.Delegate = null; _popover.ContentViewController = null!; _popover.Dispose(); }
        _popoverDelegate?.Dispose();
        _content?.Dispose();
        _viewModel?.Dispose();
        _popover = null;
        _popoverDelegate = null;
        _content = null;
        _viewModel = null;
    }

    private void DisableForSession(Exception exception, string message)
    {
        _logger.CaptureException(exception, message);
        if (!_sessionAvailable) return;
        _sessionAvailable = false;
        try { DisposeSurface(); }
        catch (Exception cleanup) { _logger.CaptureException(cleanup, "Companion surface cleanup after guarded error"); }
        Serilog.Log.Warning("{Title}: {Message}", Translator.Companion_DisabledTitle, Translator.Companion_DisabledMessage);
    }

    private void Observe(Task task)
        => _ = task.ContinueWith(t => _logger.CaptureException(t.Exception!.GetBaseException(), nameof(MacStatusItemController)),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    public void Dispose()
    {
        if (_disposed) return;
        Hide();
        DisposeSurface();
        _disposed = true;
    }

    private sealed class PopoverDelegate(Action closed) : NSPopoverDelegate
    {
        public override void DidClose(NSNotification notification) => closed();
    }
}
