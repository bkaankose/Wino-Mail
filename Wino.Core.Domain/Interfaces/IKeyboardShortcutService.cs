using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Service for managing keyboard shortcuts for mail operations.
/// </summary>
public interface IKeyboardShortcutService
{
    /// <summary>
    /// Gets all available keyboard shortcuts.
    /// </summary>
    /// <returns>Collection of keyboard shortcuts.</returns>
    Task<IEnumerable<KeyboardShortcut>> GetKeyboardShortcutsAsync();

    /// <summary>
    /// Gets enabled keyboard shortcuts only.
    /// </summary>
    /// <returns>Collection of enabled keyboard shortcuts.</returns>
    Task<IEnumerable<KeyboardShortcut>> GetEnabledKeyboardShortcutsAsync();

    /// <summary>
    /// Creates or updates a keyboard shortcut.
    /// </summary>
    /// <param name="shortcut">The keyboard shortcut to save.</param>
    /// <returns>The saved keyboard shortcut.</returns>
    Task<KeyboardShortcut> SaveKeyboardShortcutAsync(KeyboardShortcut shortcut);

    /// <summary>
    /// Deletes a keyboard shortcut.
    /// </summary>
    /// <param name="shortcutId">The ID of the shortcut to delete.</param>
    Task DeleteKeyboardShortcutAsync(Guid shortcutId);

    /// <summary>
    /// Gets the keyboard shortcut for the given key combination in a specific mode.
    /// </summary>
    /// <param name="mode">The application mode to search within.</param>
    /// <param name="key">The pressed key.</param>
    /// <param name="modifierKeys">The modifier keys pressed.</param>
    /// <returns>The matching shortcut if found, otherwise null.</returns>
    Task<KeyboardShortcut> GetShortcutForKeyAsync(WinoApplicationMode mode, string key, ModifierKeys modifierKeys);

    /// <summary>
    /// Checks if a key combination is already assigned to another shortcut.
    /// </summary>
    /// <param name="mode">The application mode to check within.</param>
    /// <param name="key">The key to check.</param>
    /// <param name="modifierKeys">The modifier keys to check.</param>
    /// <param name="excludeShortcutId">Optional ID to exclude from the check (for updates).</param>
    /// <returns>True if the combination is already used, false otherwise.</returns>
    Task<bool> IsKeyCombinationInUseAsync(WinoApplicationMode mode, string key, ModifierKeys modifierKeys, Guid? excludeShortcutId = null);

    /// <summary>
    /// Creates default keyboard shortcuts for common mail operations.
    /// </summary>
    Task CreateDefaultShortcutsAsync();

    /// <summary>
    /// Resets all shortcuts to defaults.
    /// </summary>
    Task ResetToDefaultShortcutsAsync();
}
