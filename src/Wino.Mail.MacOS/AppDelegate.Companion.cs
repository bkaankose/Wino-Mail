using AppKit;
using CommunityToolkit.Mvvm.Messaging;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Companion;
using Wino.Messaging.UI;

namespace Wino.Mail.MacOS;

/// <summary>
/// The menu bar icon with the companion popover (the Windows tray icon and companion flyout) and
/// the close behaviour of the main window (Windows ShellWindow.OnAppWindowClosing):
/// <list type="bullet">
/// <item><see cref="AppCloseBehavior.Terminate"/>: closing the main window quits Wino.</item>
/// <item><see cref="AppCloseBehavior.RunInBackgroundWithTrayIcon"/>: the window closes, the Dock
/// icon goes away (Accessory activation policy) and the menu bar icon stays. If the icon cannot be
/// created the Dock icon stays instead, so Wino is never unreachable.</item>
/// <item><see cref="AppCloseBehavior.RunInBackgroundWithoutTrayIcon"/>: the window closes and the
/// Dock icon goes away; launching Wino again (Finder, Spotlight, Launchpad) brings it back.</item>
/// </list>
/// The runtime keeps synchronizing in the background. Any activation (menu bar, relaunch,
/// notification, mailto, Dock menu) goes through <see cref="AppKitNavigationService.BringToFront"/>,
/// which restores the Regular policy. Quit (⌘Q, Exit) always quits.
/// <para>
/// Launch at login: SMAppService.MainApp starts Wino with the standard open-application Apple event
/// whose keyAEPropData parameter is keyAELaunchedAsLogInItem (the <c>--background</c> argument does
/// the same for testing). Wino then starts without a window when the close behaviour is a background
/// mode, mirroring the Windows startup task, and with its window when it is Terminate.
/// </para>
/// </summary>
public sealed partial class AppDelegate
{
    private const string BackgroundLaunchArgument = "--background";
    private readonly DateTimeOffset _sessionStartedAtUtc = DateTimeOffset.UtcNow;
    private readonly object _companionRecipient = new();
    private MacStatusItemController? _statusItem;
    private NSObject? _windowClosingObserver;
    private bool _launchedAtLogin;
    private bool _backgroundPending;

    /// <summary>True while Wino runs without a Dock icon.</summary>
    private static bool IsInBackground => NSApplication.SharedApplication.ActivationPolicy == NSApplicationActivationPolicy.Accessory;

    partial void CompanionLaunching()
    {
        _launchedAtLogin = DetectLoginLaunch();
        if (_launchedAtLogin) Serilog.Log.Information("Wino was launched at login; starting in the background until the close behaviour is known.");
    }

    /// <summary>Whether launching should keep the Dock icon away and the first window hidden.</summary>
    private bool StartsInBackground => _launchedAtLogin;

    partial void CompanionServicesReady()
    {
        if (_services is null) return;
        var preferences = _services.GetRequiredService<IPreferencesService>();
        var navigation = new CompanionNavigationCallbacks(
            _ => ShowWinoAsync(WinoApplicationMode.Mail),
            _ => ShowWinoAsync(WinoApplicationMode.Calendar),
            _ => ShowWinoAsync(WinoApplicationMode.Tasks),
            (_, _) => ShowWinoAsync(WinoApplicationMode.Mail),
            (_, mailUniqueId, _) => _notificationResponses.NavigateMailAsync(mailUniqueId),
            (_, calendarItemId, _) => _notificationResponses.NavigateCalendarItemAsync(calendarItemId),
            (_, calendarItemId, _) => _notificationResponses.JoinOnlineAsync(calendarItemId),
            (query, _) => FindContactFromCompanionAsync(query),
            (_, _) => NewMailFromDockAsync(),
            (_, _, _) => NewEventFromDockAsync(),
            _ => OpenSettingsFromCompanionAsync());

        _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            _statusItem = new MacStatusItemController(_services, _dispatcher, _sessionStartedAtUtc, navigation,
                () => ShowWinoAsync(WinoApplicationMode.Mail),
                () => ShowWinoAsync(WinoApplicationMode.Calendar),
                () => NSApplication.SharedApplication.Terminate(null));
            _statusItem.SetCompanionEnabled(preferences.IsCompanionEnabled);
            UpdateStatusItem();
            // Other windows (Settings, compose) keep the Dock icon; the last titled window closing finishes it.
            _windowClosingObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSWindow.WillCloseNotification,
                _ => NSApplication.SharedApplication.BeginInvokeOnMainThread(TryEnterBackground));
            if (_launchedAtLogin) FinishLoginLaunch(preferences.AppCloseBehavior);
        });

        preferences.PreferenceChanged += CompanionPreferenceChanged;
        WeakReferenceMessenger.Default.Register<object, AccountCreatedMessage>(_companionRecipient, (_, _) => Observe(UpdateCompanionReadinessAsync()));
        WeakReferenceMessenger.Default.Register<object, AccountRemovedMessage>(_companionRecipient, (_, _) => Observe(UpdateCompanionReadinessAsync()));
        Observe(UpdateCompanionReadinessAsync());
#if DEBUG
        RegisterCompanionDebugCommands();
#endif
    }

    partial void CompanionStopping()
    {
        WeakReferenceMessenger.Default.UnregisterAll(_companionRecipient);
        if (_services?.GetService<IPreferencesService>() is { } preferences) preferences.PreferenceChanged -= CompanionPreferenceChanged;
        _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_windowClosingObserver is not null)
            {
                NSNotificationCenter.DefaultCenter.RemoveObserver(_windowClosingObserver);
                _windowClosingObserver.Dispose();
                _windowClosingObserver = null;
            }
            // A teardown failure must not abort quitting.
            try { _statusItem?.Dispose(); }
            catch (Exception exception) { Serilog.Log.Warning(exception, "Menu bar item cleanup failed while quitting."); }
            _statusItem = null;
        });
    }

    #region Close behaviour

    /// <summary>The main window's WindowShouldClose (see <see cref="WinoShellWindow.ShouldClose"/>).</summary>
    private bool ShellWindowShouldClose(WinoShellWindow window)
    {
        if (_services is null || !_runtimeStarted || _terminating) return true;
        var behavior = _services.GetRequiredService<IPreferencesService>().AppCloseBehavior;
        switch (behavior)
        {
            case AppCloseBehavior.Terminate:
                // Quit through the normal path; the window stays if quitting is refused (account setup).
                NSApplication.SharedApplication.BeginInvokeOnMainThread(() => NSApplication.SharedApplication.Terminate(null));
                return false;
            case AppCloseBehavior.RunInBackgroundWithTrayIcon:
                if (_statusItem?.Show() != true)
                {
                    // Without the menu bar icon the Dock icon is the way back, so it stays.
                    Serilog.Log.Warning("The menu bar icon is unavailable; Wino keeps its Dock icon while in the background.");
                    _backgroundPending = false;
                    return true;
                }
                RequestBackground();
                return true;
            default:
                RequestBackground();
                return true;
        }
    }

    /// <summary>Drops the Dock icon once no titled window is left (the closing one included).</summary>
    private void RequestBackground()
    {
        _backgroundPending = true;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(TryEnterBackground);
    }

    private void TryEnterBackground()
    {
        if (!_backgroundPending || _terminating) return;
        if (HasVisibleTitledWindow()) return;
        _backgroundPending = false;
        _statusItem?.CloseCompanion();
        NSApplication.SharedApplication.ActivationPolicy = NSApplicationActivationPolicy.Accessory;
        Serilog.Log.Information("Wino is running in the background.");
    }

    private static bool HasVisibleTitledWindow()
    {
        foreach (var window in NSApplication.SharedApplication.DangerousWindows)
        {
            // A minimized window counts: it lives in the Dock, which must not go away under it.
            if ((window.IsVisible || window.IsMiniaturized) && window.StyleMask.HasFlag(NSWindowStyle.Titled) && window.SheetParent is null) return true;
        }
        return false;
    }

    /// <summary>Brings Wino back: Dock icon, menu bar and the main window in front.</summary>
    internal void ShowMainWindow()
    {
        _backgroundPending = false;
        AppKitNavigationService.BringToFront(_window);
    }

    /// <summary>A login launch decides here, once the preferences are readable, whether to stay hidden.</summary>
    private void FinishLoginLaunch(AppCloseBehavior behavior)
    {
        _launchedAtLogin = false;
        var stayHidden = behavior switch
        {
            AppCloseBehavior.RunInBackgroundWithTrayIcon => _statusItem?.IsVisible == true,
            AppCloseBehavior.RunInBackgroundWithoutTrayIcon => true,
            _ => false
        };
        if (stayHidden)
        {
            Serilog.Log.Information("Login launch: staying in the background ({Behavior}).", behavior);
            return;
        }
        ShowMainWindow();
    }

    private static bool DetectLoginLaunch()
    {
        try
        {
            if (NSProcessInfo.ProcessInfo.Arguments.Contains(BackgroundLaunchArgument)) return true;
            var appleEvent = NSAppleEventManager.SharedAppleEventManager.CurrentAppleEvent;
            if (appleEvent is null) return false;
            const uint OpenApplicationEvent = 0x6F617070;   // kAEOpenApplication 'oapp'
            const uint PropertyDataKeyword = 0x70726474;    // keyAEPropData 'prdt'
            const uint LaunchedAsLoginItem = 0x6C676974;    // keyAELaunchedAsLogInItem 'lgit'
            return (uint)appleEvent.EventID == OpenApplicationEvent
                && appleEvent.ParamDescriptorForKeyword(PropertyDataKeyword)?.EnumCodeValue() == LaunchedAsLoginItem;
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "Could not read how Wino was launched.");
            return false;
        }
    }

    #endregion

    #region Menu bar icon

    private void UpdateStatusItem()
    {
        if (_statusItem is null || _services is null) return;
        var behavior = _services.GetRequiredService<IPreferencesService>().AppCloseBehavior;
        if (behavior == AppCloseBehavior.RunInBackgroundWithTrayIcon)
        {
            if (!_statusItem.Show() && IsInBackground) ShowMainWindow();
        }
        else
        {
            _statusItem.Hide();
        }
    }

    private void CompanionPreferenceChanged(object? sender, string propertyName)
    {
        if (propertyName is not (nameof(IPreferencesService.AppCloseBehavior) or nameof(IPreferencesService.IsCompanionEnabled))) return;
        _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_statusItem is null || _services is null || _terminating) return;
            if (propertyName == nameof(IPreferencesService.IsCompanionEnabled))
                _statusItem.SetCompanionEnabled(_services.GetRequiredService<IPreferencesService>().IsCompanionEnabled);
            else
                UpdateStatusItem();
        });
    }

    private async Task UpdateCompanionReadinessAsync()
    {
        if (_services is null) return;
        var accounts = await _services.GetRequiredService<IAccountService>().GetAccountsAsync();
        var state = accounts.Count > 0 ? CompanionReadinessState.Ready : CompanionReadinessState.NoAccounts;
        await _dispatcher.ExecuteOnUIThread(() => _statusItem?.SetReadiness(state));
    }

    #endregion

    #region Companion navigation

    /// <summary>Windows ActivatePreferredWindowAsync: the shell in <paramref name="mode"/>, or Welcome without accounts.</summary>
    private async Task ShowWinoAsync(WinoApplicationMode mode)
    {
        if (_services is null) return;
        var accounts = await _services.GetRequiredService<IAccountService>().GetAccountsAsync();
        var navigation = _services.GetRequiredService<AppKitNavigationService>();
        if (accounts.Count > 0 && await navigation.EnsureShellAsync(mode)) return;
        await _dispatcher.ExecuteOnUIThread(ShowMainWindow);
    }

    /// <summary>Windows FindCompanionContactAsync: Contacts, then the matching contact selected.</summary>
    private async Task FindContactFromCompanionAsync(string? query)
    {
        await ShowWinoAsync(WinoApplicationMode.Contacts);
        if (string.IsNullOrWhiteSpace(query) || _services is null) return;
        var contacts = _services.GetRequiredService<ContactsPageViewModel>();
        var match = (await contacts.SearchContactsAsync(query, 1)).FirstOrDefault();
        if (match is not null) await contacts.LoadAndSelectContactAsync(match.Id);
    }

    /// <summary>Windows OpenCompanionSettingsAsync: the main window first, then the Settings window.</summary>
    private async Task OpenSettingsFromCompanionAsync()
    {
        await ShowWinoAsync(WinoApplicationMode.Mail);
        await _dispatcher.ExecuteOnUIThread(() => Navigate(navigation => navigation.ChangeApplicationMode(WinoApplicationMode.Settings)));
    }

    #endregion

#if DEBUG
    private void RegisterCompanionDebugCommands()
    {
        MacDebugBridge.Register("companion-toggle", async _ =>
        {
            Task toggle = Task.CompletedTask;
            await _dispatcher.ExecuteOnUIThread(() => toggle = _statusItem?.ToggleCompanionAsync() ?? Task.CompletedTask);
            await toggle;
            await Task.Delay(300);
            return _statusItem?.Describe() ?? "no status item";
        });
        MacDebugBridge.Register("companion-state", _ => Task.FromResult(_statusItem?.Describe() ?? "no status item"));
        // "statusitem [show|hide]" reports the icon and its menu; show/hide override the preference until it changes.
        MacDebugBridge.Register("statusitem", async args =>
        {
            string result = string.Empty;
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                if (_statusItem is null) { result = "no status item"; return; }
                if (args.Length > 0 && args[0].Equals("show", StringComparison.OrdinalIgnoreCase)) _statusItem.Show();
                else if (args.Length > 0 && args[0].Equals("hide", StringComparison.OrdinalIgnoreCase)) _statusItem.Hide();
                using var menu = _statusItem.BuildMenu();
                result = $"{_statusItem.Describe()} menu=[{string.Join(" | ", menu.Items.Select(item => item.IsSeparatorItem ? "—" : item.Title))}] " +
                         $"behavior={_services?.GetRequiredService<IPreferencesService>().AppCloseBehavior}";
            });
            return result;
        });
        // "close-window" runs the main window's close path as the close button would.
        MacDebugBridge.Register("close-window", async _ =>
        {
            await _dispatcher.ExecuteOnUIThread(() => _window?.PerformClose(null));
            await Task.Delay(500);
            return DescribeBackground();
        });
        MacDebugBridge.Register("reopen", async _ =>
        {
            await _dispatcher.ExecuteOnUIThread(() => ApplicationShouldHandleReopen(NSApplication.SharedApplication, false));
            await Task.Delay(300);
            return DescribeBackground();
        });
    }

    private string DescribeBackground()
        => $"policy={NSApplication.SharedApplication.ActivationPolicy} window={(_window?.IsVisible == true ? "visible" : "hidden")} " +
           $"pending={_backgroundPending} {_statusItem?.Describe()}";
#endif
}
