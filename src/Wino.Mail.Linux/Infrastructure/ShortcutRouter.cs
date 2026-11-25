using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Common;
using Wino.Platform.Linux.Services;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>What a router needs from the window that owns it. Every function is called on the main thread.</summary>
internal sealed record ShortcutRouterContext(
    Func<WinoApplicationMode?> Mode,
    Func<object?> RootViewModel,
    Func<object?> SurfaceViewModel,
    Func<KeyboardShortcutTriggerDetails, Task> ModeHook,
    bool IsPopOut = false)
{
    /// <summary>Runs a fixed key's action (<see cref="LinuxKeymap"/>); false when it did not apply here, so the key passes on.</summary>
    public Func<string, bool>? RunFixed { get; init; }

    /// <summary>Whether single-key shortcuts may act with this focus (switched on, Mail, list or reader focus).</summary>
    public Func<Gtk.Widget?, bool>? AllowsSingleKeys { get; init; }
}

internal enum ShortcutRouteResult { NoMatch, Blocked, Dispatched }

/// <summary>
/// Routes the configurable shortcuts of one window the way the Windows <c>KeyboardShortcutController</c> does: the
/// composer in the reading pane, then the reader, then the mode's page, then the mode. One capture-phase key controller
/// sees every key of the window; a gesture that an application accelerator owns, a key inside a dialog or a popover and a
/// key the text-input policy refuses all pass on to the focused widget. After the configurable shortcuts it matches the
/// fixed Context keys and the single keys of <see cref="LinuxKeymap"/>: they depend on the focus, so they cannot be
/// application accelerators, which fire in the capture phase inside dialogs, popovers and entries too.
/// </summary>
internal sealed class ShortcutRouter : IDisposable
{
    /// <summary>A web view with this class only shows text (the reader): it is text input for copy gestures only.</summary>
    public const string ReadOnlyWebClass = "wino-readonly-web";

    private readonly Gtk.Window _window;
    private readonly IKeyboardShortcutService _shortcuts;
    private readonly LinuxShortcutPlatformService _platform;
    private readonly ShortcutRouterContext _context;
    private readonly Action<Exception> _error;
    private readonly Gtk.EventControllerKey _keys = Gtk.EventControllerKey.New();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly EventHandler _changed;
    private volatile KeyboardShortcutSnapshot[]? _cache;
    private WinoApplicationMode? _cacheMode;
    private uint _heldKeyval;
    private LinuxKey? _lastFixed;
    private int _executing;
    private bool _disposed;

    public ShortcutRouter(Gtk.Window window, IKeyboardShortcutService shortcuts, LinuxShortcutPlatformService platform,
        ShortcutRouterContext context, Action<Exception> error)
    {
        _window = window;
        _shortcuts = shortcuts;
        _platform = platform;
        _context = context;
        _error = error;

        _keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        var keys = _keys;
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(
            h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h, (_, args) => OnKeyPressed(args.Keyval, args.Keycode, args.State)));
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyReleasedSignalArgs>(
            h => keys.OnKeyReleased += h, h => keys.OnKeyReleased -= h, (_, args) => OnKeyReleased(ShortcutKeys.LayoutIndependent(window.GetDisplay(), args.Keyval, args.Keycode), args.State)));
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerKey, Gtk.EventControllerKey.ModifiersSignalArgs, bool>(
            h => keys.OnModifiers += h, h => keys.OnModifiers -= h, (_, args) =>
            {
                _platform.SetShift(args.State.HasFlag(Gdk.ModifierType.ShiftMask));
                return false;
            }));
        _subscriptions.Add(GtkSignals.OnNotify(window, "is-active", () =>
        {
            if (!_window.IsActive) ResetKeys();
        }));
        window.AddController(_keys);

        // The event may come from any thread: it only drops the cache, which the main thread rebuilds lazily.
        _changed = (_, _) => _cache = null;
        shortcuts.KeyboardShortcutsChanged += _changed;
    }

    public bool IsPopOut => _context.IsPopOut;

    /// <summary>True while the app lock hides the window's content: every gesture passes on.</summary>
    public bool IsSuspended { get; set; }

    /// <summary>The shortcuts of <paramref name="mode"/> that can match a key here (no synced Command gestures).</summary>
    public KeyboardShortcutSnapshot[] ShortcutsFor(WinoApplicationMode mode)
    {
        if (_cache is { } cached && _cacheMode == mode) return cached;
        var built = _shortcuts.EnabledShortcutsSnapshot
            .Where(item => item.Mode == mode && !item.ModifierKeys.HasFlag(ModifierKeys.Command))
            .ToArray();
        _cacheMode = mode;
        _cache = built;
        return built;
    }

    /// <summary>The decision for a gesture without running it: which shortcut, and whether focus or a dialog blocks it.</summary>
    public ShortcutRouteResult Resolve(uint keyval, Gdk.ModifierType state, Gtk.Widget? focus, out KeyboardShortcutSnapshot? shortcut)
    {
        shortcut = null;
        if (_disposed || IsSuspended || _context.Mode() is not { } mode) return ShortcutRouteResult.NoMatch;
        if (HasVisibleDialog(_window) || IsInPopover(focus)) return ShortcutRouteResult.NoMatch;

        var match = FindShortcut(mode, keyval, state);
        // Shift+Delete is the mode's Delete; the request coordinator makes it a permanent delete while Shift is held.
        if (match is null && (state & Gtk.Functions.AcceleratorGetDefaultModMask()) == Gdk.ModifierType.ShiftMask && ShortcutKeys.KeyName(keyval) == "DELETE")
            match = FindShortcut(mode, keyval, 0);
        if (match is null) return ShortcutRouteResult.NoMatch;

        if (ShortcutKeys.Accelerator(match.ModifierKeys, match.Key) is { } accelerator
            && _window.GetApplication() is { } application
            && application.GetActionsForAccel(accelerator).Length > 0) return ShortcutRouteResult.NoMatch;

        if (!IsEligibleRoot(mode, _context.RootViewModel())) return ShortcutRouteResult.NoMatch;

        shortcut = match;
        var context = SurfaceContext(_context.SurfaceViewModel(), mode);
        return KeyboardShortcutContextPolicy.CanExecute(match.Action, match.Key ?? string.Empty, match.ModifierKeys, context, IsTextInput(focus, keyval, state))
            ? ShortcutRouteResult.Dispatched
            : ShortcutRouteResult.Blocked;
    }

    /// <summary>The fixed key the gesture would run here, and whether focus, mode or text input blocks it; nothing runs.</summary>
    public ShortcutRouteResult ResolveFixed(uint pressed, uint keyval, Gdk.ModifierType state, Gtk.Widget? focus, out LinuxKey? key)
    {
        key = null;
        if (_disposed || IsSuspended || _context.RunFixed is null || _context.Mode() is not { } mode) return ShortcutRouteResult.NoMatch;
        if (HasVisibleDialog(_window) || IsInPopover(focus)) return ShortcutRouteResult.NoMatch;
        var match = LinuxKeymap.Match(pressed, state) ?? (keyval != pressed ? LinuxKeymap.Match(keyval, state) : null);
        if (match is null)
        {
            if (LinuxKeymap.MatchSingle(pressed, state) is not { } single) return ShortcutRouteResult.NoMatch;
            if (single.Mode is { } singleMode && singleMode != mode) return ShortcutRouteResult.NoMatch;
            // A single key that may not act here is an ordinary key: it types, or moves the sidebar's type-ahead.
            if (IsTextInput(focus, pressed, state) || _context.AllowsSingleKeys?.Invoke(focus) != true) return ShortcutRouteResult.NoMatch;
            key = single;
            return ShortcutRouteResult.Dispatched;
        }
        if (match.Mode is { } keyMode && keyMode != mode) return ShortcutRouteResult.NoMatch;
        key = match;
        return !match.WorksInTextInput && IsTextInput(focus, pressed, state) ? ShortcutRouteResult.Blocked : ShortcutRouteResult.Dispatched;
    }

    /// <summary>The key path with the hardware key: the key is named layout-independently first.</summary>
    public ShortcutRouteResult Route(uint keyval, uint keycode, Gdk.ModifierType state, Gtk.Widget? focus)
        => Route(keyval, keycode, state, focus, simulated: false, out _);

    /// <summary>
    /// The whole key path: the configurable shortcuts, then the fixed keys and the single keys. The key handler and the
    /// Debug action use it; a simulated key holds Shift for the shortcut it runs, as the real key does.
    /// </summary>
    public ShortcutRouteResult Route(uint pressed, uint keycode, Gdk.ModifierType state, Gtk.Widget? focus, bool simulated, out string matched)
    {
        matched = "-";
        _lastFixed = null;
        var keyval = ShortcutKeys.LayoutIndependent(_window.GetDisplay(), pressed, keycode);
        var result = ShortcutRouteResult.NoMatch;
        if (ShortcutKeys.KeyName(keyval) is not null)
        {
            result = Resolve(keyval, state, focus, out var shortcut);
            if (shortcut is not null) matched = shortcut.Action.ToString();
            if (result == ShortcutRouteResult.Blocked && shortcut is not null)
                Serilog.Log.Debug("Shortcut {Action} blocked by text input or the composer.", shortcut.Action);
            if (result == ShortcutRouteResult.Dispatched && shortcut is not null)
            {
                if (simulated) _platform.SetShift(state.HasFlag(Gdk.ModifierType.ShiftMask));
                var dispatch = DispatchAsync(shortcut, focus);
                if (simulated) _ = ReleaseShiftAfterAsync(dispatch);
                return result;
            }
        }

        var fixedResult = ResolveFixed(pressed, keyval, state, focus, out var key);
        if (key is null) return result;
        matched = key.Action;
        if (fixedResult != ShortcutRouteResult.Dispatched) return fixedResult;
        _lastFixed = key;
        return RunFixed(key) ? ShortcutRouteResult.Dispatched : ShortcutRouteResult.NoMatch;
    }

    /// <summary>The menu path: runs the active mode's action without a key, so without the text-input policy.</summary>
    public async Task<bool> DispatchActionAsync(KeyboardShortcutAction action)
    {
        if (_disposed || _context.Mode() is not { } mode || !IsEligibleRoot(mode, _context.RootViewModel())) return false;
        var shortcut = ShortcutsFor(mode).FirstOrDefault(item => item.Action == action)
            ?? new KeyboardShortcutSnapshot(Guid.Empty, mode, string.Empty, ModifierKeys.None, action, DateTime.UtcNow);
        return await DispatchAsync(shortcut, _window.GetFocus());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shortcuts.KeyboardShortcutsChanged -= _changed;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _window.RemoveController(_keys);
        _platform.SetShift(false);
    }

    private bool OnKeyPressed(uint pressed, uint keycode, Gdk.ModifierType state)
    {
        try
        {
            var keyval = ShortcutKeys.LayoutIndependent(_window.GetDisplay(), pressed, keycode);
            _platform.SetShift(state.HasFlag(Gdk.ModifierType.ShiftMask));
            // A held key repeats its press; Windows' pressed-key guard runs a shortcut once per press. Moving through
            // items and zooming repeat on purpose, so they never become the held key.
            if (_heldKeyval != 0 && _heldKeyval == keyval) return true;
            var result = Route(pressed, keycode, state, _window.GetFocus(), simulated: false, out _);
            if (result == ShortcutRouteResult.Dispatched && _lastFixed is not { Repeats: true }) _heldKeyval = keyval;
            return result == ShortcutRouteResult.Dispatched;
        }
        catch (Exception exception)
        {
            _error(exception);
            return false;
        }
    }

    private void OnKeyReleased(uint keyval, Gdk.ModifierType state)
    {
        if (_heldKeyval == keyval) _heldKeyval = 0;
        if (ShortcutKeys.KeyName(keyval) is not null) _platform.SetShift(state.HasFlag(Gdk.ModifierType.ShiftMask));
    }

    private void ResetKeys()
    {
        _heldKeyval = 0;
        _platform.SetShift(false);
    }

    private KeyboardShortcutSnapshot? FindShortcut(WinoApplicationMode mode, uint keyval, Gdk.ModifierType state)
    {
        foreach (var candidate in ShortcutsFor(mode))
        {
            if (ShortcutKeys.Matches(candidate, keyval, state)) return candidate;
        }
        return null;
    }

    private bool RunFixed(LinuxKey key)
    {
        try
        {
            if (key.Configurable is { } action)
            {
                Serilog.Log.Debug("Single key {Key} -> {Action}", key.FirstAccelerator, action);
                _ = DispatchActionAsync(action);
                return true;
            }
            var handled = _context.RunFixed?.Invoke(key.Action) == true;
            Serilog.Log.Debug("Fixed key {Action} -> {Handled}", key.Action, handled);
            return handled;
        }
        catch (Exception exception)
        {
            _error(exception);
            return false;
        }
    }

    private async Task ReleaseShiftAfterAsync(Task dispatch)
    {
        try { await dispatch; }
        finally { _platform.SetShift(false); }
    }

    private async Task<bool> DispatchAsync(KeyboardShortcutSnapshot shortcut, Gtk.Widget? focus)
    {
        if (Interlocked.Exchange(ref _executing, 1) != 0) return false;
        try
        {
            var surface = _context.SurfaceViewModel();
            var root = _context.RootViewModel();
            var popOut = _context.IsPopOut;

            if (surface is ComposePageViewModel compose)
            {
                Log(shortcut, popOut ? KeyboardShortcutInputContext.PopOutCompose : KeyboardShortcutInputContext.Compose);
                await compose.KeyboardShortcutHook(Details(shortcut, focus, popOut ? KeyboardShortcutInputContext.PopOutCompose : KeyboardShortcutInputContext.Compose));
                return true;
            }

            if (surface is MailRenderingPageViewModel reader)
            {
                var readerDetails = Details(shortcut, focus, popOut ? KeyboardShortcutInputContext.PopOutReader : KeyboardShortcutInputContext.Reader);
                Log(shortcut, readerDetails.InputContext);
                await reader.KeyboardShortcutHook(readerDetails);
                if (readerDetails.Handled || popOut) return true;
            }

            var details = Details(shortcut, focus, RootContext(shortcut.Mode));
            if (surface is not MailRenderingPageViewModel) Log(shortcut, details.InputContext);
            if (root is CoreBaseViewModel page && !ReferenceEquals(root, surface)) await page.KeyboardShortcutHook(details);
            if (!details.Handled && !popOut) await _context.ModeHook(details);
            return true;
        }
        catch (Exception exception)
        {
            _error(exception);
            return false;
        }
        finally
        {
            Volatile.Write(ref _executing, 0);
        }
    }

    private KeyboardShortcutTriggerDetails Details(KeyboardShortcutSnapshot shortcut, Gtk.Widget? focus, KeyboardShortcutInputContext context) => new()
    {
        ShortcutId = shortcut.Id,
        Mode = shortcut.Mode,
        Action = shortcut.Action,
        Key = shortcut.Key ?? string.Empty,
        ModifierKeys = shortcut.ModifierKeys,
        InputContext = context,
        Sender = _window,
        Origin = (object?)focus ?? _window
    };

    private static void Log(KeyboardShortcutSnapshot shortcut, KeyboardShortcutInputContext context)
        => Serilog.Log.Debug("Shortcut {Action} in {Mode} -> {Context}", shortcut.Action, shortcut.Mode, context);

    private static bool IsEligibleRoot(WinoApplicationMode mode, object? root) => mode switch
    {
        WinoApplicationMode.Contacts => root is ContactsPageViewModel,
        WinoApplicationMode.Tasks => root is ToDoPageViewModel,
        _ => true
    };

    private KeyboardShortcutInputContext SurfaceContext(object? surface, WinoApplicationMode mode) => surface switch
    {
        ComposePageViewModel => _context.IsPopOut ? KeyboardShortcutInputContext.PopOutCompose : KeyboardShortcutInputContext.Compose,
        MailRenderingPageViewModel => _context.IsPopOut ? KeyboardShortcutInputContext.PopOutReader : KeyboardShortcutInputContext.Reader,
        _ => RootContext(mode)
    };

    private static KeyboardShortcutInputContext RootContext(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Calendar => KeyboardShortcutInputContext.Calendar,
        WinoApplicationMode.Contacts => KeyboardShortcutInputContext.Contacts,
        WinoApplicationMode.Tasks => KeyboardShortcutInputContext.Tasks,
        _ => KeyboardShortcutInputContext.List
    };

    private static bool HasVisibleDialog(Gtk.Window window) => window switch
    {
        Adw.ApplicationWindow application => application.GetVisibleDialog() is not null,
        Adw.Window adw => adw.GetVisibleDialog() is not null,
        _ => false
    };

    private static bool IsInPopover(Gtk.Widget? focus)
    {
        for (var widget = focus; widget is not null; widget = widget.GetParent())
        {
            if (widget is Gtk.Popover) return true;
        }
        return false;
    }

    /// <summary>Entries, text views, drop-downs and the editor's web view (compared by name: this file must not load WebKit).</summary>
    internal static bool IsTextInput(Gtk.Widget? focus) => IsTextInput(focus, 0, 0);

    /// <summary>As <see cref="IsTextInput(Gtk.Widget?)"/>, except that the reader's web view (<see cref="ReadOnlyWebClass"/>) is text input only for Ctrl+A, Ctrl+C and Ctrl+Insert.</summary>
    internal static bool IsTextInput(Gtk.Widget? focus, uint keyval, Gdk.ModifierType state)
    {
        for (var widget = focus; widget is not null; widget = widget.GetParent())
        {
            if (widget is Gtk.Editable or Gtk.TextView or Gtk.DropDown or Adw.ComboRow) return true;
            if (widget.GetType().FullName == "WebKit.WebView") return !widget.HasCssClass(ReadOnlyWebClass) || IsCopyGesture(keyval, state);
        }
        return false;
    }

    private static bool IsCopyGesture(uint keyval, Gdk.ModifierType state)
    {
        if ((state & Gtk.Functions.AcceleratorGetDefaultModMask() & ~Gdk.ModifierType.ShiftMask) != Gdk.ModifierType.ControlMask) return false;
        return Gdk.Functions.KeyvalToLower(keyval) is Gdk.Constants.KEY_a or Gdk.Constants.KEY_c or Gdk.Constants.KEY_Insert or Gdk.Constants.KEY_KP_Insert;
    }
}
