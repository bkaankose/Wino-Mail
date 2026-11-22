using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Wino.Presentation.Linux.Native;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// The key names shortcuts are stored with (Windows <c>VirtualKey</c> names, upper case) and their GTK forms. The router
/// and the Keyboard shortcuts settings page both name keys here, so they agree by construction. A key that names no
/// shortcut key as pressed (a shifted digit, a letter of a non-Latin layout) is named after what the same hardware key
/// produces unshifted in the first layout that gives a usable key, as Windows virtual keys and macOS key codes do.
/// </summary>
internal static class ShortcutKeys
{
    /// <summary>The stored name of a pressed key ("N", "NUMBER1", "DELETE", "ENTER", "F5"), or null for keys shortcuts cannot use.</summary>
    public static string? KeyName(uint keyval)
    {
        var upper = Gdk.Functions.KeyvalToUpper(keyval);
        var name = Gdk.Functions.KeyvalName(upper);
        switch (name)
        {
            case "Return" or "KP_Enter" or "ISO_Enter": return "ENTER";
            case "Delete" or "KP_Delete": return "DELETE";
            case "BackSpace": return "BACK";
            case "Escape": return "ESCAPE";
            case "Tab" or "ISO_Left_Tab": return "TAB";
            case "space": return "SPACE";
            case "Left" or "Right" or "Up" or "Down" or "Home" or "End" or "Insert": return name.ToUpperInvariant();
            case "Page_Up": return "PAGEUP";
            case "Page_Down": return "PAGEDOWN";
        }

        if (name is { Length: >= 2 and <= 3 } && name[0] == 'F' && int.TryParse(name.AsSpan(1), out var function) && function is >= 1 and <= 24)
            return name;
        if (name is { Length: 4 } && name.StartsWith("KP_", StringComparison.Ordinal) && char.IsAsciiDigit(name[3]))
            return "NUMBERPAD" + name[3];

        var character = Gdk.Functions.KeyvalToUnicode(upper);
        if (character is >= 'A' and <= 'Z') return ((char)character).ToString();
        if (character is >= '0' and <= '9') return "NUMBER" + (char)character;
        return null;
    }

    /// <summary>
    /// <paramref name="keyval"/> when it names a key; otherwise the unshifted key of <paramref name="keycode"/> in the first
    /// keyboard layout where it names one (Ctrl+Shift+1 reports "exclam", a Cyrillic layout reports Cyrillic letters).
    /// </summary>
    public static uint LayoutIndependent(Gdk.Display? display, uint keyval, uint keycode)
    {
        if (KeyName(keyval) is not null || display is null || keycode == 0) return keyval;
        for (var group = 0; group < MaxLayouts; group++)
        {
            var translated = GdkKeys.TranslateKey(display, keycode, group);
            if (translated != 0 && KeyName(translated) is not null) return translated;
        }
        return keyval;
    }

    /// <summary>The shared service's normalisation plus BACKSPACE to BACK and a bare digit to NUMBERn.</summary>
    public static string Normalize(string? storedKey)
    {
        var key = storedKey?.Trim().ToUpperInvariant() ?? string.Empty;
        return key switch
        {
            "DEL" => "DELETE",
            "RETURN" => "ENTER",
            "ESC" => "ESCAPE",
            "SPACEBAR" => "SPACE",
            "BACKSPACE" => "BACK",
            { Length: 1 } when char.IsAsciiDigit(key[0]) => "NUMBER" + key,
            _ => key
        };
    }

    public static ModifierKeys Modifiers(Gdk.ModifierType state)
    {
        state &= Gtk.Functions.AcceleratorGetDefaultModMask();
        var modifiers = ModifierKeys.None;
        if (state.HasFlag(Gdk.ModifierType.ControlMask)) modifiers |= ModifierKeys.Control;
        if (state.HasFlag(Gdk.ModifierType.AltMask)) modifiers |= ModifierKeys.Alt;
        if (state.HasFlag(Gdk.ModifierType.ShiftMask)) modifiers |= ModifierKeys.Shift;
        if (state.HasFlag(Gdk.ModifierType.SuperMask)) modifiers |= ModifierKeys.Windows;
        return modifiers;
    }

    public static Gdk.ModifierType State(ModifierKeys modifiers)
    {
        var state = (Gdk.ModifierType)0;
        if (modifiers.HasFlag(ModifierKeys.Control)) state |= Gdk.ModifierType.ControlMask;
        if (modifiers.HasFlag(ModifierKeys.Alt)) state |= Gdk.ModifierType.AltMask;
        if (modifiers.HasFlag(ModifierKeys.Shift)) state |= Gdk.ModifierType.ShiftMask;
        if (modifiers.HasFlag(ModifierKeys.Windows)) state |= Gdk.ModifierType.SuperMask;
        return state;
    }

    public static bool Matches(KeyboardShortcutSnapshot shortcut, uint keyval, Gdk.ModifierType state)
        => !shortcut.ModifierKeys.HasFlag(ModifierKeys.Command)
           && shortcut.ModifierKeys == Modifiers(state)
           && KeyName(keyval) is { } name
           && name == Normalize(shortcut.Key);

    /// <summary>A GTK accelerator such as "&lt;Control&gt;&lt;Shift&gt;g", or null when the stored key has no GDK key or needs Command.</summary>
    public static string? Accelerator(ModifierKeys modifiers, string? storedKey)
    {
        if (modifiers.HasFlag(ModifierKeys.Command)) return null;
        var keyval = Keyval(storedKey);
        return keyval == 0 ? null : Gtk.Functions.AcceleratorName(keyval, State(modifiers));
    }

    /// <summary>The GDK key of a stored name; 0 when it has none.</summary>
    public static uint Keyval(string? storedKey)
    {
        var key = Normalize(storedKey);
        var name = key switch
        {
            "" => null,
            "ENTER" => "Return",
            "DELETE" => "Delete",
            "BACK" => "BackSpace",
            "ESCAPE" => "Escape",
            "TAB" => "Tab",
            "SPACE" => "space",
            "LEFT" => "Left",
            "RIGHT" => "Right",
            "UP" => "Up",
            "DOWN" => "Down",
            "HOME" => "Home",
            "END" => "End",
            "INSERT" => "Insert",
            "PAGEUP" => "Page_Up",
            "PAGEDOWN" => "Page_Down",
            { Length: 7 } when key.StartsWith("NUMBER", StringComparison.Ordinal) && char.IsAsciiDigit(key[6]) => key[6].ToString(),
            { Length: 10 } when key.StartsWith("NUMBERPAD", StringComparison.Ordinal) && char.IsAsciiDigit(key[9]) => "KP_" + key[9],
            { Length: 1 } when char.IsAsciiLetter(key[0]) => key.ToLowerInvariant(),
            { Length: >= 2 and <= 3 } when key[0] == 'F' && int.TryParse(key.AsSpan(1), out var function) && function is >= 1 and <= 24 => key,
            _ => null
        };
        if (name is null) return 0;
        var keyval = Gdk.Functions.KeyvalFromName(name);
        return keyval == VoidSymbol ? 0 : keyval;
    }

    /// <summary>Parses "&lt;Control&gt;&lt;Shift&gt;g" or "Delete" by hand: Gir.Core 0.8.1 has no public <c>gtk_accelerator_parse</c>.</summary>
    public static bool TryParseAccelerator(string accelerator, out uint keyval, out Gdk.ModifierType state)
    {
        keyval = 0;
        state = 0;
        var rest = accelerator.AsSpan().Trim();
        while (rest.Length > 0 && rest[0] == '<')
        {
            var end = rest.IndexOf('>');
            if (end < 0) return false;
            var modifier = rest[1..end].ToString().ToLowerInvariant();
            state |= modifier switch
            {
                "control" or "ctrl" or "primary" => Gdk.ModifierType.ControlMask,
                "shift" => Gdk.ModifierType.ShiftMask,
                "alt" or "mod1" => Gdk.ModifierType.AltMask,
                "super" => Gdk.ModifierType.SuperMask,
                _ => (Gdk.ModifierType)0
            };
            rest = rest[(end + 1)..];
        }
        if (rest.Length == 0) return false;
        keyval = Gdk.Functions.KeyvalFromName(rest.ToString());
        return keyval != 0 && keyval != VoidSymbol;
    }

    private const uint VoidSymbol = 0xffffff;
    private const int MaxLayouts = 4;
}
