using Foundation;
using UIKit;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.UIKit.Pad.Shell;

/// <summary>
/// Maps stored keyboard shortcut keys (Windows VirtualKey names, as Settings records them) onto <see cref="UIKeyCommand"/>
/// inputs and modifier flags (the UIKit counterpart of the Mac ShortcutKeys and AppDelegate.TryGetKeyEquivalent). The
/// Windows Delete key is the iPad ⌫ key, as on the Mac.
/// </summary>
internal static class ShortcutKeys
{
    /// <summary>The key command input for a stored key; null when the key has no hardware keyboard input.</summary>
    public static string? Input(string? storedKey)
    {
        var name = storedKey?.Trim().ToUpperInvariant() ?? string.Empty;
        return name switch
        {
            "ENTER" or "RETURN" => "\r",
            "TAB" => "\t",
            "SPACE" or "SPACEBAR" => " ",
            "BACK" or "BACKSPACE" or "DELETE" or "DEL" => "\b",
            "ESCAPE" or "ESC" => UIKeyCommand.Escape.ToString(),
            "UP" => UIKeyCommand.UpArrow.ToString(),
            "DOWN" => UIKeyCommand.DownArrow.ToString(),
            "LEFT" => UIKeyCommand.LeftArrow.ToString(),
            "RIGHT" => UIKeyCommand.RightArrow.ToString(),
            { Length: 1 } single when char.IsLetterOrDigit(single[0]) => single.ToLowerInvariant(),
            { Length: 7 } number when number.StartsWith("NUMBER", StringComparison.Ordinal) && char.IsDigit(number[6]) => number[6].ToString(),
            _ => null
        };
    }

    public static UIKeyModifierFlags Modifiers(ModifierKeys modifiers)
    {
        UIKeyModifierFlags flags = 0;
        if (modifiers.HasFlag(ModifierKeys.Command)) flags |= UIKeyModifierFlags.Command;
        if (modifiers.HasFlag(ModifierKeys.Control)) flags |= UIKeyModifierFlags.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) flags |= UIKeyModifierFlags.Alternate;
        if (modifiers.HasFlag(ModifierKeys.Shift)) flags |= UIKeyModifierFlags.Shift;
        return flags;
    }

    /// <summary>True when the shortcut carries Command or Control, so it can replace a built-in Cmd shortcut.</summary>
    public static bool HasCommandModifier(ModifierKeys modifiers)
        => modifiers.HasFlag(ModifierKeys.Command) || modifiers.HasFlag(ModifierKeys.Control);

    public static NSString AsInput(string input) => new(input);
}
