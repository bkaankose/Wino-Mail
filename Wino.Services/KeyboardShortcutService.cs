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
/// Service for managing keyboard shortcuts for mail operations.
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
            "SELECT * FROM KeyboardShortcut ORDER BY MailOperation");
    }

    /// <summary>
    /// Gets enabled keyboard shortcuts only.
    /// </summary>
    public async Task<IEnumerable<KeyboardShortcut>> GetEnabledKeyboardShortcutsAsync()
    {
        return await Connection.QueryAsync<KeyboardShortcut>(
            "SELECT * FROM KeyboardShortcut WHERE IsEnabled = ? ORDER BY MailOperation",
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
    /// Gets the mail operation for the given key combination.
    /// </summary>
    public async Task<MailOperation?> GetMailOperationForKeyAsync(string key, ModifierKeys modifierKeys)
    {
        const string query = "SELECT * FROM KeyboardShortcut WHERE Key = ? AND ModifierKeys = ? AND IsEnabled = ? LIMIT 1";
        var shortcut = await Connection.FindWithQueryAsync<KeyboardShortcut>(query, key, (int)modifierKeys, 1);
        return shortcut?.MailOperation;
    }

    /// <summary>
    /// Checks if a key combination is already assigned to another shortcut.
    /// </summary>
    public async Task<bool> IsKeyCombinationInUseAsync(string key, ModifierKeys modifierKeys, Guid? excludeShortcutId = null)
    {
        string query;
        KeyboardShortcut shortcut;
        
        if (excludeShortcutId.HasValue)
        {
            query = "SELECT * FROM KeyboardShortcut WHERE Key = ? AND ModifierKeys = ? AND Id != ? LIMIT 1";
            shortcut = await Connection.FindWithQueryAsync<KeyboardShortcut>(query, key, (int)modifierKeys, excludeShortcutId.Value);
        }
        else
        {
            query = "SELECT * FROM KeyboardShortcut WHERE Key = ? AND ModifierKeys = ? LIMIT 1";
            shortcut = await Connection.FindWithQueryAsync<KeyboardShortcut>(query, key, (int)modifierKeys);
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
            var exists = await IsKeyCombinationInUseAsync(shortcut.Key, shortcut.ModifierKeys);
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
                MailOperation = MailOperation.SoftDelete,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "Delete",
                ModifierKeys = ModifierKeys.Shift,
                MailOperation = MailOperation.HardDelete,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "A",
                ModifierKeys = ModifierKeys.Control,
                MailOperation = MailOperation.Archive,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "R",
                ModifierKeys = ModifierKeys.Control,
                MailOperation = MailOperation.MarkAsRead,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "U",
                ModifierKeys = ModifierKeys.Control,
                MailOperation = MailOperation.MarkAsUnread,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "F",
                ModifierKeys = ModifierKeys.Control,
                MailOperation = MailOperation.SetFlag,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "F",
                ModifierKeys = ModifierKeys.Control | ModifierKeys.Shift,
                MailOperation = MailOperation.ClearFlag,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "J",
                ModifierKeys = ModifierKeys.Control,
                MailOperation = MailOperation.MoveToJunk,
                IsEnabled = true
            },
            new KeyboardShortcut
            {
                Id = Guid.NewGuid(),
                Key = "M",
                ModifierKeys = ModifierKeys.Control,
                MailOperation = MailOperation.Move,
                IsEnabled = true
            }
        };
    }
}