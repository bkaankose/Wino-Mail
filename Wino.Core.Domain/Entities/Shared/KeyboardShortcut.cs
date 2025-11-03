using System;
using System.ComponentModel.DataAnnotations;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

/// <summary>
/// Represents a user-defined keyboard shortcut for mail operations.
/// </summary>
public class KeyboardShortcut
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The key combination string (e.g., "D", "Delete", "F1").
    /// </summary>
    public string Key { get; set; }

    /// <summary>
    /// The modifier keys for this shortcut.
    /// </summary>
    public ModifierKeys ModifierKeys { get; set; }

    /// <summary>
    /// The mail operation this shortcut triggers.
    /// </summary>
    public MailOperation MailOperation { get; set; }

    /// <summary>
    /// Whether this shortcut is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// When this shortcut was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// User-friendly display name for the shortcut.
    /// </summary>
    public string DisplayName 
    { 
        get 
        {
            var modifierText = string.Empty;
            if (ModifierKeys.HasFlag(ModifierKeys.Control))
                modifierText += "Ctrl+";
            if (ModifierKeys.HasFlag(ModifierKeys.Alt))
                modifierText += "Alt+";
            if (ModifierKeys.HasFlag(ModifierKeys.Shift))
                modifierText += "Shift+";
            if (ModifierKeys.HasFlag(ModifierKeys.Windows))
                modifierText += "Win+";

            return modifierText + Key;
        } 
    }
}