using AppKit;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// The key names Mac keyboard shortcuts are stored with. The Settings shortcut recorder and the mail
/// list's Delete shortcut matcher both name keys here, so they agree by construction. Letters and
/// digits use the typed character (layout aware), not the virtual key code.
/// </summary>
internal static class ShortcutKeys
{
    internal static string? KeyName(NSEvent theEvent)
    {
        switch (theEvent.KeyCode)
        {
            case 36: return "Enter";
            case 48: return "Tab";
            case 49: return "Space";
            case 51: return "Back";
            case 117: return "Delete";
            case 53: return "Escape";
            case 123: return "Left";
            case 124: return "Right";
            case 125: return "Down";
            case 126: return "Up";
        }
        var characters = theEvent.CharactersIgnoringModifiers;
        if (string.IsNullOrEmpty(characters)) return null;
        var character = char.ToUpperInvariant(characters[0]);
        if (char.IsLetter(character)) return character.ToString();
        if (char.IsDigit(character)) return "Number" + character;
        return null;
    }

    /// <summary>
    /// The Mac menu notation of a stored shortcut: modifier symbols in the system order (⌃⌥⇧⌘) followed by the key,
    /// for example ⇧⌘G or ⌘⌫. The shared DisplayName keeps the Windows "Ctrl+Shift+G" form.
    /// </summary>
    internal static string Display(ModifierKeys modifiers, string? key)
    {
        var symbols = (modifiers.HasFlag(ModifierKeys.Control) ? "⌃" : string.Empty)
            + (modifiers.HasFlag(ModifierKeys.Alt) ? "⌥" : string.Empty)
            + (modifiers.HasFlag(ModifierKeys.Shift) ? "⇧" : string.Empty)
            + (modifiers.HasFlag(ModifierKeys.Command) || modifiers.HasFlag(ModifierKeys.Windows) ? "⌘" : string.Empty);
        return symbols + KeySymbol(key);
    }

    private static string KeySymbol(string? key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        switch (key.ToUpperInvariant())
        {
            // A stored DELETE also fires on ⌫ (AppDelegate.KeyMatches), the key Mac keyboards label delete.
            case "BACK" or "BACKSPACE" or "DELETE" or "DEL": return "⌫";
            case "ENTER" or "RETURN": return "↩";
            case "TAB": return "⇥";
            case "ESCAPE" or "ESC": return "⎋";
            case "SPACE" or "SPACEBAR": return "Space";
            case "LEFT": return "←";
            case "RIGHT": return "→";
            case "UP": return "↑";
            case "DOWN": return "↓";
        }
        if (key.Length == 7 && key.StartsWith("Number", StringComparison.OrdinalIgnoreCase)) return key[6..];
        return key.Length == 1 ? key.ToUpperInvariant() : key;
    }
}
