using AppKit;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;

namespace Wino.Mail.MacOS;

/// <summary>
/// Main menu items whose action has a configurable shortcut (Settings › Keyboard shortcuts) show and
/// answer to the user's Mail-mode shortcut. Mac shortcuts are recorded with their native modifiers
/// (Command, Control, Option, Shift), so they map one to one onto the menu's modifier mask. An item keeps
/// its built-in key equivalent while no usable shortcut exists for its action. Shortcuts without Command
/// or Control are left to the views (a bare Delete must keep working in text fields).
/// </summary>
public sealed partial class AppDelegate
{
    private sealed record ShortcutMenuItem(KeyboardShortcutAction Action, NSMenuItem Item, string DefaultKey, NSEventModifierMask DefaultMask);

    private readonly List<ShortcutMenuItem> _shortcutMenuItems = [];
    private IKeyboardShortcutService? _shortcutService;

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
        NSApplication.SharedApplication.BeginInvokeOnMainThread(ApplyShortcutMenus);
    }

    partial void ShortcutMenusStopping()
    {
        if (_shortcutService is not null) _shortcutService.KeyboardShortcutsChanged -= KeyboardShortcutsChanged;
        _shortcutService = null;
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
