using AppKit;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Platform.MacOS.Services;

namespace Wino.Mail.MacOS;

/// <summary>
/// The companion's global shortcut (Windows App.TryConfigureCompanionHotKey): registered at
/// startup when enabled, re-applied when its preferences change, removed when quitting. Pressing
/// it toggles the companion under the menu bar icon; without the icon it brings Wino to the front.
/// </summary>
public sealed partial class AppDelegate
{
    private MacCompanionHotKeyController? _companionHotKey;

    /// <summary>UI thread, after the status item exists.</summary>
    private void StartCompanionHotKey()
    {
        if (_services is null || _companionHotKey is not null) return;
        _companionHotKey = _services.GetRequiredService<MacCompanionHotKeyController>();
        _companionHotKey.Pressed += CompanionHotKeyPressed;
        _companionHotKey.Start();
#if DEBUG
        RegisterHotKeyDebugCommands();
        // The pop-out windows' debug command lives with them; it needs the services only.
        Views.Calendar.EventDetailsWindow.RegisterDebugCommands(_services);
#endif
    }

    /// <summary>UI thread, while quitting.</summary>
    private void StopCompanionHotKey()
    {
        if (_companionHotKey is null) return;
        _companionHotKey.Pressed -= CompanionHotKeyPressed;
        try { _companionHotKey.Suspend(); }
        catch (Exception exception) { Serilog.Log.Warning(exception, "Removing the companion shortcut failed while quitting."); }
        _companionHotKey = null;
    }

    private void CompanionHotKeyPreferenceChanged(string propertyName)
    {
        if (propertyName is not (nameof(IPreferencesService.IsCompanionHotKeyEnabled) or nameof(IPreferencesService.CompanionHotKeyKey)
            or nameof(IPreferencesService.CompanionHotKeyModifiers) or nameof(IPreferencesService.IsCompanionEnabled))) return;
        _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_terminating || _companionHotKey is null) return;
            _companionHotKey.Reload();
        });
    }

    private void CompanionHotKeyPressed(object? sender, EventArgs args)
    {
        if (_terminating) return;
        if (_statusItem is { IsVisible: true, IsCompanionAvailable: true } statusItem)
        {
            Observe(statusItem.ToggleCompanionAsync());
            return;
        }
        ShowMainWindow();
    }

#if DEBUG
    private void RegisterHotKeyDebugCommands()
    {
        // "hotkey-status" reports the stored, registered gesture and the last registration error.
        MacDebugBridge.Register("hotkey-status", async _ =>
        {
            string result = string.Empty;
            await _dispatcher.ExecuteOnUIThread(() =>
            {
                var preferences = _services!.GetRequiredService<IPreferencesService>();
                var controller = _companionHotKey;
                result = $"enabled={preferences.IsCompanionHotKeyEnabled} companion={preferences.IsCompanionEnabled} " +
                         $"stored={MacHotKeyKeys.Format(new(preferences.CompanionHotKeyKey, preferences.CompanionHotKeyModifiers))} " +
                         $"({preferences.CompanionHotKeyModifiers}+{preferences.CompanionHotKeyKey}) " +
                         $"registered={(controller?.ActiveHotKey is { } active ? MacHotKeyKeys.Format(active) : "none")} " +
                         $"error={controller?.LastError ?? "none"}";
            });
            return result;
        });
        // "hotkey-fire" runs the pressed handler as the system would.
        MacDebugBridge.Register("hotkey-fire", async _ =>
        {
            await _dispatcher.ExecuteOnUIThread(() => CompanionHotKeyPressed(this, EventArgs.Empty));
            await Task.Delay(400);
            return _statusItem?.Describe() ?? "no status item";
        });
    }
#endif
}
