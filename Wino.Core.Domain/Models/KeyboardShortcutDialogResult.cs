using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models;

/// <summary>
/// Result returned from keyboard shortcut dialog.
/// </summary>
public class KeyboardShortcutDialogResult
{
    /// <summary>
    /// Whether the dialog was completed successfully.
    /// </summary>
    public bool IsSuccess { get; set; }

    /// <summary>
    /// The application mode selected by the user.
    /// </summary>
    public WinoApplicationMode Mode { get; set; } = WinoApplicationMode.Mail;

    /// <summary>
    /// The key combination entered by the user.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The modifier keys selected by the user.
    /// </summary>
    public ModifierKeys ModifierKeys { get; set; }

    /// <summary>
    /// The shortcut action selected by the user.
    /// </summary>
    public KeyboardShortcutAction Action { get; set; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static KeyboardShortcutDialogResult Success(WinoApplicationMode mode, string key, ModifierKeys modifierKeys, KeyboardShortcutAction action)
    {
        return new KeyboardShortcutDialogResult
        {
            IsSuccess = true,
            Mode = mode,
            Key = key,
            ModifierKeys = modifierKeys,
            Action = action
        };
    }

    /// <summary>
    /// Creates a canceled result.
    /// </summary>
    public static KeyboardShortcutDialogResult Canceled()
    {
        return new KeyboardShortcutDialogResult
        {
            IsSuccess = false
        };
    }
}
