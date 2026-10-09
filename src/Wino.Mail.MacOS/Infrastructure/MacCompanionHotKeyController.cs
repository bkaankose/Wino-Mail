using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Platform.MacOS.Services;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// The companion's global shortcut (Windows MainTrayController + App.TryConfigureCompanionHotKey):
/// the Carbon hotkey is registered only while both the companion and its shortcut are enabled.
/// <see cref="TryConfigure"/> applies a candidate before the caller stores it, so a conflict leaves
/// the previous shortcut registered. Main thread only.
/// </summary>
public sealed class MacCompanionHotKeyController(MacGlobalHotKeyService hotKeys, IPreferencesService preferences) : IDisposable
{
    private bool _isCompanionEnabled;
    private bool _isHotKeyEnabled;
    private HotKeyGesture _hotKey = HotKeyGesture.Default;
    private bool _started;

    /// <summary>Raised on the main thread when the shortcut is pressed.</summary>
    public event EventHandler? Pressed
    {
        add => hotKeys.Pressed += value;
        remove => hotKeys.Pressed -= value;
    }

    public HotKeyGesture? ActiveHotKey => hotKeys.ActiveHotKey;

    public string? LastError => hotKeys.LastError;

    /// <summary>The stored shortcut, whether or not it is registered.</summary>
    public HotKeyGesture StoredGesture => new(preferences.CompanionHotKeyKey, preferences.CompanionHotKeyModifiers);

    /// <summary>Registers the stored shortcut at startup. Returns false when it could not be registered.</summary>
    public bool Start()
    {
        _started = true;
        _isCompanionEnabled = preferences.IsCompanionEnabled;
        var registered = TryConfigure(preferences.IsCompanionHotKeyEnabled, StoredGesture);
        if (!registered) Serilog.Log.Warning("The companion shortcut could not be registered: {Error}", hotKeys.LastError);
        return registered;
    }

    public void SetCompanionEnabled(bool enabled)
    {
        _isCompanionEnabled = enabled;
        if (_started) hotKeys.TrySetHotKey(enabled && _isHotKeyEnabled ? _hotKey : null);
    }

    /// <summary>Applies <paramref name="enabled"/> and <paramref name="gesture"/>; false when the gesture is invalid or taken.</summary>
    public bool TryConfigure(bool enabled, HotKeyGesture gesture)
    {
        var normalized = gesture.Normalize();
        if (enabled && !normalized.IsValid) return false;
        if (_isCompanionEnabled && !hotKeys.TrySetHotKey(enabled ? normalized : null)) return false;
        _isHotKeyEnabled = enabled;
        _hotKey = normalized;
        return true;
    }

    /// <summary>Re-reads the preferences after they changed elsewhere (backup restore, another window).</summary>
    public void Reload()
    {
        if (!_started) return;
        _isCompanionEnabled = preferences.IsCompanionEnabled;
        TryConfigure(preferences.IsCompanionHotKeyEnabled, StoredGesture);
    }

    /// <summary>Removes the system registration while a recorder listens, so the current shortcut can be captured.</summary>
    public void Suspend() => hotKeys.TrySetHotKey(null);

    public void Dispose() => hotKeys.Dispose();
}
