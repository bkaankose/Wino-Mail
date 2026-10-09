using AppKit;

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
}
