using AppKit;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Platform.MacOS.Services;

namespace Wino.Mail.MacOS;

/// <summary>
/// Main menu items whose action has a configurable shortcut (Settings › Keyboard shortcuts) show and
/// answer to the user's Mail-mode shortcut. Mac shortcuts are recorded with their native modifiers
/// (Command, Control, Option, Shift), so they map one to one onto the menu's modifier mask. An item keeps
/// its built-in key equivalent while no usable shortcut exists for its action. Shortcuts without Command
/// or Control are left to the views (a bare Delete must keep working in text fields).
/// <para>
/// Calendar, Contacts and To Do have no menu items for their shortcuts. A key-down monitor on the main
/// window routes those modes' configured shortcuts (Windows KeyboardShortcutController): it matches the
/// active mode's enabled shortcuts, applies <see cref="KeyboardShortcutContextPolicy"/> (no bare keys in
/// text input), and hands the action to the content page's ViewModel, then the mode's provider. File › New
/// follows the active mode (New Event, New Contact, New Task) while its menu is open.
/// </para>
/// </summary>
public sealed partial class AppDelegate
{
    private sealed record ShortcutMenuItem(KeyboardShortcutAction Action, NSMenuItem Item, string DefaultKey, NSEventModifierMask DefaultMask);

    private readonly List<ShortcutMenuItem> _shortcutMenuItems = [];
    private IKeyboardShortcutService? _shortcutService;
    private NSObject? _shortcutMonitor;
    private int _shortcutExecuting;
    private NSMenuItem? _newItemMenuItem;
    private FileMenuDelegate? _fileMenuDelegate;

    /// <summary>Registers an installed menu item for <paramref name="action"/>; returns the item.</summary>
    private NSMenuItem TrackShortcut(NSMenuItem item, KeyboardShortcutAction action, bool install)
    {
        if (install) _shortcutMenuItems.Add(new ShortcutMenuItem(action, item, item.KeyEquivalent, item.KeyEquivalentModifierMask));
        return item;
    }

    partial void ShortcutMenusServicesReady()
    {
        _shortcutService = _services?.GetService<IKeyboardShortcutService>();
        if (_shortcutService is null) return;

        _shortcutService.KeyboardShortcutsChanged += KeyboardShortcutsChanged;
        NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
        {
            ApplyShortcutMenus();
            if (_terminating || _shortcutMonitor is not null) return;
            _shortcutMonitor = NSEvent.AddLocalMonitorForEventsMatchingMask(NSEventMask.KeyDown, RouteModeShortcut);
#if DEBUG
            RegisterShortcutDebugCommands();
#endif
        });
    }

    partial void ShortcutMenusStopping()
    {
        if (_shortcutService is not null) _shortcutService.KeyboardShortcutsChanged -= KeyboardShortcutsChanged;
        _shortcutService = null;
        if (_shortcutMonitor is not null)
        {
            NSEvent.RemoveMonitor(_shortcutMonitor);
            _shortcutMonitor = null;
        }
    }

    // The service raises the event on the thread that saved the shortcut.
    private void KeyboardShortcutsChanged(object? sender, EventArgs args)
        => NSApplication.SharedApplication.BeginInvokeOnMainThread(ApplyShortcutMenus);

    private void ApplyShortcutMenus()
    {
        if (_terminating) return;

        var snapshot = _shortcutService?.EnabledShortcutsSnapshot ?? [];
        foreach (var entry in _shortcutMenuItems)
        {
            var key = entry.DefaultKey;
            var mask = entry.DefaultMask;

            foreach (var shortcut in snapshot)
            {
                if (shortcut.Mode != WinoApplicationMode.Mail || shortcut.Action != entry.Action) continue;
                if (!TryGetKeyEquivalent(shortcut, out var customKey, out var customMask)) continue;

                key = customKey;
                mask = customMask;
                break;
            }

            if (entry.Item.KeyEquivalent != key) entry.Item.KeyEquivalent = key;
            if (entry.Item.KeyEquivalentModifierMask != mask) entry.Item.KeyEquivalentModifierMask = mask;
        }
    }

    #region Mode shortcut router (Calendar, Contacts, To Do)

    /// <summary>Local key-down monitor: consumes the event when it triggers one of the active mode's shortcuts.</summary>
    private NSEvent? RouteModeShortcut(NSEvent theEvent)
    {
        try
        {
            if (!TryMatchModeShortcut(theEvent, out var shell, out var shortcut)) return theEvent;
            // A held key repeats the event; the shortcut runs once, like Windows' pressed-key guard.
            if (!theEvent.IsARepeat) DispatchModeShortcut(shell, shortcut);
            return null;
        }
        catch (Exception error)
        {
            Serilog.Log.Error(error, "Keyboard shortcut routing failed.");
            return theEvent;
        }
    }

    private bool TryMatchModeShortcut(NSEvent theEvent, out Views.WinoAppShellViewController shell, out KeyboardShortcutSnapshot shortcut)
    {
        shell = null!;
        shortcut = null!;
        if (_terminating || _shortcutService is null) return false;
        var application = NSApplication.SharedApplication;
        if (application.ModalWindow is not null) return false;
        // Menu tracking (and other event tracking) runs its own loop; keys there belong to it.
        if (NSRunLoop.Current.CurrentMode == NSRunLoopMode.EventTracking.GetConstant()) return false;
        if (Shell() is not { } current || theEvent.Window is not { } window || window.Handle != current.View.Window?.Handle) return false;
        if (window.AttachedSheet is not null) return false;
        if (current.ActiveMode is not { } mode || ShortcutContextFor(mode) is not { } context) return false;
        if (!IsEligibleRoot(mode, current.ContentViewModel)) return false;

        var keyName = MacHotKeyKeys.NameForKeyCode(theEvent.KeyCode);
        var modifiers = ModifiersOf(theEvent.ModifierFlags);
        var isTextInput = IsTextInput(window.FirstResponder);
        foreach (var candidate in _shortcutService.EnabledShortcutsSnapshot)
        {
            if (candidate.Mode != mode || candidate.ModifierKeys != modifiers || !KeyMatches(candidate.Key, keyName, theEvent.KeyCode)) continue;
            if (!KeyboardShortcutContextPolicy.CanExecute(candidate.Action, candidate.Key ?? string.Empty, candidate.ModifierKeys, context, isTextInput)) return false;
            shell = current;
            shortcut = candidate;
            return true;
        }
        return false;
    }

    private async void DispatchModeShortcut(Views.WinoAppShellViewController shell, KeyboardShortcutSnapshot shortcut)
    {
        if (Interlocked.Exchange(ref _shortcutExecuting, 1) != 0) return;
        try
        {
            await shell.RouteKeyboardShortcutAsync(new KeyboardShortcutTriggerDetails
            {
                ShortcutId = shortcut.Id,
                Mode = shortcut.Mode,
                Action = shortcut.Action,
                Key = shortcut.Key ?? string.Empty,
                ModifierKeys = shortcut.ModifierKeys,
                InputContext = ShortcutContextFor(shortcut.Mode) ?? KeyboardShortcutInputContext.List,
                Sender = shell.View.Window!,
                Origin = shell.View.Window?.FirstResponder!
            });
        }
        catch (Exception error)
        {
            ReportError(error);
        }
        finally
        {
            Volatile.Write(ref _shortcutExecuting, 0);
        }
    }

    /// <summary>Runs the active mode's action for a menu choice (no key involved, so no text-input policy).</summary>
    private void DispatchModeAction(Views.WinoAppShellViewController shell, WinoApplicationMode mode, KeyboardShortcutAction action)
    {
        if (!IsEligibleRoot(mode, shell.ContentViewModel)) return;
        var configured = _shortcutService?.EnabledShortcutsSnapshot.FirstOrDefault(item => item.Mode == mode && item.Action == action);
        DispatchModeShortcut(shell, configured ?? new KeyboardShortcutSnapshot(Guid.Empty, mode, string.Empty, ModifierKeys.None, action, DateTime.UtcNow));
    }

    private static KeyboardShortcutInputContext? ShortcutContextFor(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Calendar => KeyboardShortcutInputContext.Calendar,
        WinoApplicationMode.Contacts => KeyboardShortcutInputContext.Contacts,
        WinoApplicationMode.Tasks => KeyboardShortcutInputContext.Tasks,
        _ => null
    };

    /// <summary>Windows IsEligibleRootSurface: People and To Do only route while their page is the content.</summary>
    private static bool IsEligibleRoot(WinoApplicationMode mode, object? contentViewModel) => mode switch
    {
        WinoApplicationMode.Contacts => contentViewModel is ContactsPageViewModel,
        WinoApplicationMode.Tasks => contentViewModel is ToDoPageViewModel,
        _ => true
    };

    private static ModifierKeys ModifiersOf(NSEventModifierMask flags)
    {
        var modifiers = ModifierKeys.None;
        if (flags.HasFlag(NSEventModifierMask.CommandKeyMask)) modifiers |= ModifierKeys.Command;
        if (flags.HasFlag(NSEventModifierMask.ControlKeyMask)) modifiers |= ModifierKeys.Control;
        if (flags.HasFlag(NSEventModifierMask.AlternateKeyMask)) modifiers |= ModifierKeys.Alt;
        if (flags.HasFlag(NSEventModifierMask.ShiftKeyMask)) modifiers |= ModifierKeys.Shift;
        return modifiers;
    }

    /// <summary>
    /// Compares a stored key (Windows VirtualKey name) with the pressed key. The Windows Delete key is the Mac
    /// ⌫ key in practice, so a stored DELETE also matches ⌫; the keypad Enter matches ENTER.
    /// </summary>
    internal static bool KeyMatches(string? storedKey, string? pressedName, ushort keyCode)
    {
        var stored = NormalizeShortcutKey(storedKey);
        if (stored.Length == 0) return false;
        if (pressedName is not null && stored == NormalizeShortcutKey(pressedName)) return true;
        return (stored == "DELETE" && keyCode == 0x33) || (stored == "ENTER" && keyCode == 0x4C);
    }

    private static string NormalizeShortcutKey(string? key)
    {
        var normalized = key?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized switch
        {
            "DEL" => "DELETE",
            "RETURN" => "ENTER",
            "ESC" => "ESCAPE",
            "SPACEBAR" => "SPACE",
            "BACKSPACE" => "BACK",
            { Length: 1 } digit when char.IsDigit(digit[0]) => "NUMBER" + digit,
            _ => normalized
        };
    }

    /// <summary>Text input: a text view or field editor, an edited text or search field, or anything inside a web view.</summary>
    private static bool IsTextInput(NSResponder? responder)
    {
        if (responder is NSText) return true;
        if (responder is NSTextField field && (field.Editable || field.CurrentEditor is not null)) return true;
        for (var view = responder as NSView; view is not null; view = view.Superview)
            if (view is WebKit.WKWebView) return true;
        return false;
    }

    #endregion

    #region File › New follows the mode

    /// <summary>Keeps the File › New item; its title and key equivalent follow the active mode while the menu is open.</summary>
    private void AttachFileMenu(NSMenu file, NSMenuItem newItem)
    {
        _newItemMenuItem = newItem;
        _fileMenuDelegate = new FileMenuDelegate(this);
        file.Delegate = _fileMenuDelegate;
    }

    /// <summary>
    /// File › New. Mail keeps its pane entry (Shell.NewItem). Other modes run their "new" shortcut action,
    /// so To Do focuses the quick-add box and Calendar opens a new event (Windows Ctrl+N). A key press in
    /// another mode is the router's job: it reaches here only when that mode's shortcut is a different key.
    /// </summary>
    private void NewItemForActiveMode()
    {
        if (Shell() is not { } shell) return;
        var mode = shell.ActiveMode ?? WinoApplicationMode.Mail;
        if (NewActionFor(mode) is not { } action) { shell.NewItem(); return; }
        if (IsNewItemKeyEquivalent(NSApplication.SharedApplication.CurrentEvent)) return;
        DispatchModeAction(shell, mode, action);
    }

    /// <summary>
    /// True when <paramref name="current"/> is the File › New key equivalent itself (the Mail mapping, since the
    /// menu is closed). Any other key event reaching the action is a menu choice from a keyboard-opened menu
    /// (Full Keyboard Access, VoiceOver) and must run.
    /// </summary>
    private bool IsNewItemKeyEquivalent(NSEvent? current)
    {
        if (current?.Type != NSEventType.KeyDown || _newItemMenuItem is not { } item || string.IsNullOrEmpty(item.KeyEquivalent)) return false;
        const NSEventModifierMask relevant = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask |
                                             NSEventModifierMask.AlternateKeyMask | NSEventModifierMask.ShiftKeyMask;
        return string.Equals(current.CharactersIgnoringModifiers, item.KeyEquivalent, StringComparison.OrdinalIgnoreCase) &&
               (current.ModifierFlags & relevant) == (item.KeyEquivalentModifierMask & relevant);
    }

    private static KeyboardShortcutAction? NewActionFor(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Calendar => KeyboardShortcutAction.NewEvent,
        WinoApplicationMode.Contacts => KeyboardShortcutAction.NewContact,
        WinoApplicationMode.Tasks => KeyboardShortcutAction.NewTask,
        _ => null
    };

    private static string NewTitleFor(WinoApplicationMode mode) => mode switch
    {
        WinoApplicationMode.Calendar => Translator.CalendarEventCompose_NewEventButton,
        WinoApplicationMode.Contacts => Translator.KeyboardShortcuts_ActionNewContact,
        WinoApplicationMode.Tasks => Translator.KeyboardShortcuts_ActionNewTask,
        _ => Translator.MenuNewMail
    };

    private void FileMenuWillOpen()
    {
        if (_newItemMenuItem is not { } item || Shell()?.ActiveMode is not { } mode || NewActionFor(mode) is not { } action) return;
        item.Title = NewTitleFor(mode);
        var key = string.Empty;
        NSEventModifierMask mask = 0;
        var configured = _shortcutService?.EnabledShortcutsSnapshot.FirstOrDefault(shortcut => shortcut.Mode == mode && shortcut.Action == action);
        if (configured is not null && TryGetKeyEquivalent(configured, out var customKey, out var customMask)) { key = customKey; mask = customMask; }
        item.KeyEquivalent = key;
        item.KeyEquivalentModifierMask = mask;
    }

    /// <summary>Restores the Mail title; <see cref="ApplyShortcutMenus"/> stays the only writer of the Mail key equivalents.</summary>
    private void FileMenuDidClose()
    {
        if (_newItemMenuItem is not { } item) return;
        if (item.Title != Translator.MenuNewMail) item.Title = Translator.MenuNewMail;
        ApplyShortcutMenus();
    }

    private sealed class FileMenuDelegate(AppDelegate owner) : NSMenuDelegate
    {
        public override void MenuWillOpen(NSMenu menu) => owner.FileMenuWillOpen();

        public override void MenuDidClose(NSMenu menu) => owner.FileMenuDidClose();

        public override void MenuWillHighlightItem(NSMenu menu, NSMenuItem? item) { }
    }

    #endregion

#if DEBUG
    private void RegisterShortcutDebugCommands()
    {
        // "shortcut-list": the active mode's shortcuts and the File › New item.
        MacDebugBridge.Register("shortcut-list", _ =>
        {
            var mode = Shell()?.ActiveMode;
            var shortcuts = (_shortcutService?.EnabledShortcutsSnapshot ?? []).Where(item => item.Mode == mode)
                .Select(item => $"{item.Action}={string.Concat(MacHotKeyKeys.ModifierSymbols(item.ModifierKeys))}{MacHotKeyKeys.Display(item.Key)}");
            if (_newItemMenuItem is not null) FileMenuWillOpen();
            var file = _newItemMenuItem is { } menuItem ? $"'{menuItem.Title}' key='{menuItem.KeyEquivalent}' mask={menuItem.KeyEquivalentModifierMask}" : "none";
            if (_newItemMenuItem is not null) FileMenuDidClose();
            return Task.FromResult($"mode={mode} content={Shell()?.ContentViewModel?.GetType().Name} {string.Join(" ", shortcuts)} fileNew={file}");
        });
        // "shortcut-fire ACTION": runs the active mode's action through the router path, without a key.
        MacDebugBridge.Register("shortcut-fire", args =>
        {
            if (Shell() is not { } shell || shell.ActiveMode is not { } mode) return Task.FromResult("no shell");
            if (args.Length == 0 || !Enum.TryParse<KeyboardShortcutAction>(args[0], true, out var action)) return Task.FromResult("usage: shortcut-fire ACTION");
            if (!IsEligibleRoot(mode, shell.ContentViewModel)) return Task.FromResult("content page is not eligible");
            DispatchModeAction(shell, mode, action);
            return Task.FromResult($"dispatched {action} in {mode}");
        });
        // "shortcut-match KEY [MODS…]": which shortcut a gesture triggers in the active mode, without and with text focus.
        MacDebugBridge.Register("shortcut-match", args =>
        {
            if (args.Length == 0) return Task.FromResult("usage: shortcut-match KEY [command] [control] [alt] [shift]");
            var mode = Shell()?.ActiveMode;
            var modifiers = ModifierKeys.None;
            foreach (var arg in args.Skip(1))
                if (Enum.TryParse<ModifierKeys>(arg, true, out var modifier)) modifiers |= modifier;
            MacHotKeyKeys.TryGetKeyCode(args[0], out var keyCode);
            var match = (_shortcutService?.EnabledShortcutsSnapshot ?? []).FirstOrDefault(item =>
                item.Mode == mode && item.ModifierKeys == modifiers && KeyMatches(item.Key, MacHotKeyKeys.NameForKeyCode(keyCode) ?? args[0], keyCode));
            if (match is null || mode is not { } active || ShortcutContextFor(active) is not { } context) return Task.FromResult($"no match in {mode}");
            var plain = KeyboardShortcutContextPolicy.CanExecute(match.Action, match.Key ?? string.Empty, match.ModifierKeys, context, false);
            var text = KeyboardShortcutContextPolicy.CanExecute(match.Action, match.Key ?? string.Empty, match.ModifierKeys, context, true);
            return Task.FromResult($"{match.Action} in {mode}: allowed={plain} inTextInput={text}");
        });
    }
#endif

    /// <summary>Maps a stored shortcut onto an AppKit key equivalent; false when the menu should not own it.</summary>
    internal static bool TryGetKeyEquivalent(KeyboardShortcutSnapshot shortcut, out string key, out NSEventModifierMask mask)
    {
        key = string.Empty;
        mask = 0;

        if (shortcut.ModifierKeys.HasFlag(ModifierKeys.Command)) mask |= NSEventModifierMask.CommandKeyMask;
        if (shortcut.ModifierKeys.HasFlag(ModifierKeys.Control)) mask |= NSEventModifierMask.ControlKeyMask;
        if (shortcut.ModifierKeys.HasFlag(ModifierKeys.Alt)) mask |= NSEventModifierMask.AlternateKeyMask;
        if (shortcut.ModifierKeys.HasFlag(ModifierKeys.Shift)) mask |= NSEventModifierMask.ShiftKeyMask;

        if ((mask & (NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ControlKeyMask)) == 0) return false;

        var name = shortcut.Key?.Trim().ToUpperInvariant() ?? string.Empty;
        key = name switch
        {
            "ENTER" or "RETURN" => "\r",
            "TAB" => "\t",
            "SPACE" => " ",
            "BACK" or "BACKSPACE" => "\u0008",
            "DELETE" or "DEL" => "\uF728",
            "ESCAPE" or "ESC" => "\u001b",
            "UP" => "\uF700",
            "DOWN" => "\uF701",
            "LEFT" => "\uF702",
            "RIGHT" => "\uF703",
            { Length: 1 } single when char.IsLetterOrDigit(single[0]) => single.ToLowerInvariant(),
            { Length: 7 } number when number.StartsWith("NUMBER", StringComparison.Ordinal) && char.IsDigit(number[6]) => number[6].ToString(),
            { Length: >= 2 and <= 3 } function when function[0] == 'F' && int.TryParse(function[1..], out var index) && index is >= 1 and <= 20
                => ((char)(0xF704 + index - 1)).ToString(),
            _ => string.Empty
        };

        return key.Length > 0;
    }
}
