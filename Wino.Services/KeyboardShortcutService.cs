using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Extensions;

namespace Wino.Services;

/// <summary>
/// Service for managing keyboard shortcuts for mail and calendar actions.
/// </summary>
public class KeyboardShortcutService : BaseDatabaseService, IKeyboardShortcutService
{
    public KeyboardShortcutService(IDatabaseService databaseService) : base(databaseService)
    {
    }

    /// <summary>
    /// Gets all available keyboard shortcuts.
    /// </summary>
    public async Task<IEnumerable<KeyboardShortcut>> GetKeyboardShortcutsAsync()
    {
        return await Connection.QueryAsync<KeyboardShortcut>(
            "SELECT * FROM KeyboardShortcut ORDER BY Mode, Action");
    }

    /// <summary>
    /// Gets enabled keyboard shortcuts only.
    /// </summary>
    public async Task<IEnumerable<KeyboardShortcut>> GetEnabledKeyboardShortcutsAsync()
    {
        return await Connection.QueryAsync<KeyboardShortcut>(
            "SELECT * FROM KeyboardShortcut WHERE IsEnabled = ? ORDER BY Mode, Action",
            true);
    }

    /// <summary>
    /// Creates or updates a keyboard shortcut.
    /// </summary>
    public async Task<KeyboardShortcut> SaveKeyboardShortcutAsync(KeyboardShortcut shortcut)
    {
        if (shortcut.Id == Guid.Empty)
        {
            shortcut.Id = Guid.NewGuid();
            shortcut.CreatedAt = DateTime.UtcNow;
            await Connection.InsertAsync(shortcut, typeof(KeyboardShortcut));
        }
        else
        {
            await Connection.UpdateAsync(shortcut, typeof(KeyboardShortcut));
        }

        return shortcut;
    }

    /// <summary>
    /// Deletes a keyboard shortcut.
    /// </summary>
    public async Task DeleteKeyboardShortcutAsync(Guid shortcutId)
    {
        await Connection.ExecuteAsync($"DELETE FROM {nameof(KeyboardShortcut)} WHERE {nameof(KeyboardShortcut.Id)} = ?", shortcutId);
    }

    /// <summary>
    /// Gets the shortcut for the given key combination.
    /// </summary>
    public async Task<KeyboardShortcut> GetShortcutForKeyAsync(WinoApplicationMode mode, string key, ModifierKeys modifierKeys)
    {
        const string query = "SELECT * FROM KeyboardShortcut WHERE Mode = ? AND Key = ? AND ModifierKeys = ? AND IsEnabled = ? LIMIT 1";
        return await Connection.FindWithQueryAsync<KeyboardShortcut>(query, (int)mode, key, (int)modifierKeys, 1);
    }

    /// <summary>
    /// Checks if a key combination is already assigned to another shortcut.
    /// </summary>
    public async Task<bool> IsKeyCombinationInUseAsync(WinoApplicationMode mode, string key, ModifierKeys modifierKeys, Guid? excludeShortcutId = null)
    {
        string query;
        KeyboardShortcut shortcut;

        if (excludeShortcutId.HasValue)
        {
            query = "SELECT * FROM KeyboardShortcut WHERE Mode = ? AND Key = ? AND ModifierKeys = ? AND Id != ? LIMIT 1";
            shortcut = await Connection.FindWithQueryAsync<KeyboardShortcut>(query, (int)mode, key, (int)modifierKeys, excludeShortcutId.Value);
        }
        else
        {
            query = "SELECT * FROM KeyboardShortcut WHERE Mode = ? AND Key = ? AND ModifierKeys = ? LIMIT 1";
            shortcut = await Connection.FindWithQueryAsync<KeyboardShortcut>(query, (int)mode, key, (int)modifierKeys);
        }

        return shortcut != null;
    }

    /// <summary>
    /// Creates default keyboard shortcuts for common mail operations.
    /// </summary>
    public async Task CreateDefaultShortcutsAsync()
    {
        var defaultShortcuts = GetDefaultShortcuts();

        foreach (var shortcut in defaultShortcuts)
        {
            // Only create if it doesn't exist already
            var exists = await IsKeyCombinationInUseAsync(shortcut.Mode, shortcut.Key, shortcut.ModifierKeys);
            if (!exists)
            {
                await SaveKeyboardShortcutAsync(shortcut);
            }
        }
    }

    /// <summary>
    /// Resets all shortcuts to defaults.
    /// </summary>
    public async Task ResetToDefaultShortcutsAsync()
    {
        // Delete all existing shortcuts
        await Connection.ExecuteAsync($"DELETE FROM {nameof(KeyboardShortcut)}");

        // Create default shortcuts
        await CreateDefaultShortcutsAsync();
    }

    /// <summary>
    /// Gets the default keyboard shortcuts.
    /// </summary>
    private static List<KeyboardShortcut> GetDefaultShortcuts()
    {
        return new List<KeyboardShortcut>
        {
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "Delete",
                ModifierKeys = ModifierKeys.None,
                Mode = WinoApplicationMode.Mail,
                Action = KeyboardShortcutAction.Delete,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "N",
                ModifierKeys = ModifierKeys.Control,
                Mode = WinoApplicationMode.Mail,
                Action = KeyboardShortcutAction.NewMail,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "A",
                ModifierKeys = ModifierKeys.Control,
                Mode = WinoApplicationMode.Mail,
                Action = KeyboardShortcutAction.ToggleArchive,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "R",
                ModifierKeys = ModifierKeys.Control,
                Mode = WinoApplicationMode.Mail,
                Action = KeyboardShortcutAction.ToggleReadUnread,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "F",
                ModifierKeys = ModifierKeys.Control,
                Mode = WinoApplicationMode.Mail,
                Action = KeyboardShortcutAction.ToggleFlag,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "M",
                ModifierKeys = ModifierKeys.Control,
                Mode = WinoApplicationMode.Mail,
                Action = KeyboardShortcutAction.Move,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "R",
                ModifierKeys = ModifierKeys.Control,
                Mode = WinoApplicationMode.Mail,
                Action = KeyboardShortcutAction.Reply,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "R",
                ModifierKeys = ModifierKeys.Control | ModifierKeys.Shift,
                Mode = WinoApplicationMode.Mail,
                Action = KeyboardShortcutAction.ReplyAll,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "Enter",
                ModifierKeys = ModifierKeys.Control,
                Mode = WinoApplicationMode.Mail,
                Action = KeyboardShortcutAction.Send,
                IsEnabled = true
            }
        };
    }
}
