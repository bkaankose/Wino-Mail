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
    /// The key combination entered by the user.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The modifier keys selected by the user.
    /// </summary>
    public ModifierKeys ModifierKeys { get; set; }

    /// <summary>
    /// The mail operation selected by the user.
    /// </summary>
    public MailOperation MailOperation { get; set; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static KeyboardShortcutDialogResult Success(string key, ModifierKeys modifierKeys, MailOperation mailOperation)
    {
        return new KeyboardShortcutDialogResult
        {
            IsSuccess = true,
            Key = key,
            ModifierKeys = modifierKeys,
            MailOperation = mailOperation
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