using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
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
        using var context = ContextFactory.CreateDbContext();
        
        return await context.KeyboardShortcuts
            .OrderBy(k => k.MailOperation)
            .ToListAsync();
    }

    /// <summary>
    /// Gets enabled keyboard shortcuts only.
    /// </summary>
    public async Task<IEnumerable<KeyboardShortcut>> GetEnabledKeyboardShortcutsAsync()
    {
        using var context = ContextFactory.CreateDbContext();
        
        return await context.KeyboardShortcuts
            .Where(k => k.IsEnabled)
            .OrderBy(k => k.MailOperation)
            .ToListAsync();
    }

    /// <summary>
    /// Creates or updates a keyboard shortcut.
    /// </summary>
    public async Task<KeyboardShortcut> SaveKeyboardShortcutAsync(KeyboardShortcut shortcut)
    {
        using var context = ContextFactory.CreateDbContext();
        
        if (shortcut.Id == Guid.Empty)
        {
            shortcut.Id = Guid.NewGuid();
            shortcut.CreatedAt = DateTime.UtcNow;
            context.KeyboardShortcuts.Add(shortcut);
        }
        else
        {
            context.KeyboardShortcuts.Update(shortcut);
        }

        await context.SaveChangesAsync();
        return shortcut;
    }

    /// <summary>
    /// Deletes a keyboard shortcut.
    /// </summary>
    public async Task DeleteKeyboardShortcutAsync(Guid shortcutId)
    {
        using var context = ContextFactory.CreateDbContext();
        
        await context.KeyboardShortcuts
            .Where(k => k.Id == shortcutId)
            .ExecuteDeleteAsync();
    }

    /// <summary>
    /// Gets the mail operation for the given key combination.
    /// </summary>
    public async Task<MailOperation?> GetMailOperationForKeyAsync(string key, ModifierKeys modifierKeys)
    {
        using var context = ContextFactory.CreateDbContext();
        
        var shortcut = await context.KeyboardShortcuts
            .Where(k => k.Key == key && k.ModifierKeys == modifierKeys && k.IsEnabled)
            .FirstOrDefaultAsync();
            
        return shortcut?.MailOperation;
    }

    /// <summary>
    /// Checks if a key combination is already assigned to another shortcut.
    /// </summary>
    public async Task<bool> IsKeyCombinationInUseAsync(string key, ModifierKeys modifierKeys, Guid? excludeShortcutId = null)
    {
        using var context = ContextFactory.CreateDbContext();
        
        var query = context.KeyboardShortcuts
            .Where(k => k.Key == key && k.ModifierKeys == modifierKeys);

        if (excludeShortcutId.HasValue)
        {
            query = query.Where(k => k.Id != excludeShortcutId.Value);
        }

        var shortcut = await query.FirstOrDefaultAsync();
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
        using var context = ContextFactory.CreateDbContext();
        
        // Delete all existing shortcuts
        await context.KeyboardShortcuts.ExecuteDeleteAsync();

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