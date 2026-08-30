using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;

namespace Wino.Services;

/// <summary>
/// Owns persisted shortcuts and publishes an immutable enabled snapshot for input routing.
/// </summary>
public sealed class KeyboardShortcutService : BaseDatabaseService, IKeyboardShortcutService
{
    private static readonly WinoApplicationMode[] SupportedModes =
    [
        WinoApplicationMode.Mail,
        WinoApplicationMode.Calendar,
        WinoApplicationMode.Contacts,
        WinoApplicationMode.Tasks
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<KeyboardShortcutSnapshot> _enabledShortcutsSnapshot = Array.Empty<KeyboardShortcutSnapshot>();
    private bool _isInitialized;

    public KeyboardShortcutService(IDatabaseService databaseService) : base(databaseService)
    {
    }

    public event EventHandler KeyboardShortcutsChanged;

    public IReadOnlyList<KeyboardShortcutSnapshot> EnabledShortcutsSnapshot
        => Volatile.Read(ref _enabledShortcutsSnapshot);

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_isInitialized)
                return;

            await RepairAndSeedAsync().ConfigureAwait(false);
            await RefreshSnapshotCoreAsync().ConfigureAwait(false);
            _isInitialized = true;
        }
        finally
        {
            _gate.Release();
        }

        KeyboardShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IEnumerable<KeyboardShortcut>> GetKeyboardShortcutsAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        return await Connection.QueryAsync<KeyboardShortcut>(
            "SELECT * FROM KeyboardShortcut ORDER BY Mode, Action").ConfigureAwait(false);
    }

    public async Task<IEnumerable<KeyboardShortcut>> GetEnabledKeyboardShortcutsAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        return EnabledShortcutsSnapshot.Select(ToEntity).ToList();
    }

    public async Task<KeyboardShortcut> SaveKeyboardShortcutAsync(KeyboardShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(shortcut);

        shortcut.Key = NormalizeKey(shortcut.Key);
        shortcut.ModifierKeys &= ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows;

        if (!IsShortcutAllowed(shortcut))
            throw new InvalidOperationException("Shortcut is reserved or unsafe for this action.");

        await EnsureInitializedAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            await Connection.RunInTransactionAsync(transaction =>
            {
                var duplicate = transaction.Query<KeyboardShortcut>(
                        "SELECT * FROM KeyboardShortcut WHERE Mode = ? AND Key = ? COLLATE NOCASE AND ModifierKeys = ? AND Id != ? LIMIT 1",
                        (int)shortcut.Mode,
                        shortcut.Key,
                        (int)shortcut.ModifierKeys,
                        shortcut.Id)
                    .FirstOrDefault();

                if (duplicate is not null)
                    throw new InvalidOperationException("This key combination is already assigned in the selected mode.");

                var stored = shortcut.Id == Guid.Empty ? null : transaction.Find<KeyboardShortcut>(shortcut.Id);
                if (stored is null)
                {
                    if (shortcut.Id == Guid.Empty)
                        shortcut.Id = Guid.NewGuid();
                    shortcut.CreatedAt = DateTime.UtcNow;
                    transaction.Insert(shortcut, typeof(KeyboardShortcut));
                }
                else
                {
                    transaction.Update(shortcut, typeof(KeyboardShortcut));
                }
            }).ConfigureAwait(false);

            await RefreshSnapshotCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        KeyboardShortcutsChanged?.Invoke(this, EventArgs.Empty);
        return shortcut;
    }

    public async Task UpdateKeyboardShortcutEnabledAsync(Guid shortcutId, bool isEnabled)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            await Connection.ExecuteAsync(
                $"UPDATE {nameof(KeyboardShortcut)} SET {nameof(KeyboardShortcut.IsEnabled)} = ? WHERE {nameof(KeyboardShortcut.Id)} = ?",
                isEnabled,
                shortcutId).ConfigureAwait(false);
            await RefreshSnapshotCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        KeyboardShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteKeyboardShortcutAsync(Guid shortcutId)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            await Connection.ExecuteAsync(
                $"DELETE FROM {nameof(KeyboardShortcut)} WHERE {nameof(KeyboardShortcut.Id)} = ?",
                shortcutId).ConfigureAwait(false);
            await RefreshSnapshotCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        KeyboardShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<KeyboardShortcut> GetShortcutForKeyAsync(
        WinoApplicationMode mode,
        string key,
        ModifierKeys modifierKeys)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        var normalizedKey = NormalizeKey(key);

        var snapshot = EnabledShortcutsSnapshot.FirstOrDefault(shortcut =>
            shortcut.Mode == mode &&
            shortcut.ModifierKeys == modifierKeys &&
            string.Equals(shortcut.Key, normalizedKey, StringComparison.Ordinal));
        return snapshot is null ? null : ToEntity(snapshot);
    }

    public async Task<bool> IsKeyCombinationInUseAsync(
        WinoApplicationMode mode,
        string key,
        ModifierKeys modifierKeys,
        Guid? excludeShortcutId = null)
    {
        if (IsReservedShortcut(mode, key, modifierKeys))
            return true;

        await EnsureInitializedAsync().ConfigureAwait(false);
        var normalizedKey = NormalizeKey(key);
        var shortcuts = await Connection.QueryAsync<KeyboardShortcut>(
            "SELECT * FROM KeyboardShortcut WHERE Mode = ? AND Key = ? COLLATE NOCASE AND ModifierKeys = ?",
            (int)mode,
            normalizedKey,
            (int)modifierKeys).ConfigureAwait(false);

        return shortcuts.Any(shortcut => shortcut.Id != excludeShortcutId);
    }

    public bool IsReservedShortcut(WinoApplicationMode mode, string key, ModifierKeys modifierKeys)
    {
        if (modifierKeys.HasFlag(ModifierKeys.Windows))
            return true;

        return mode == WinoApplicationMode.Mail &&
               modifierKeys == ModifierKeys.Control &&
               string.Equals(NormalizeKey(key), "Z", StringComparison.Ordinal);
    }

    public bool IsShortcutAllowed(KeyboardShortcut shortcut)
    {
        if (shortcut is null || string.IsNullOrWhiteSpace(NormalizeKey(shortcut.Key)) ||
            IsReservedShortcut(shortcut.Mode, shortcut.Key, shortcut.ModifierKeys))
        {
            return false;
        }

        if (!IsActionAvailableInMode(shortcut.Mode, shortcut.Action))
            return false;

        if (shortcut.Action != KeyboardShortcutAction.Send)
            return true;

        var key = NormalizeKey(shortcut.Key);
        if (!shortcut.ModifierKeys.HasFlag(ModifierKeys.Control))
            return false;

        return key is not ("A" or "B" or "C" or "F" or "I" or "K" or "N" or "O" or "P" or "R" or "S" or "U" or "V" or "W" or "X" or "Y" or "Z" or
            "BACK" or "BACKSPACE" or "DELETE" or "DOWN" or "END" or "ESCAPE" or "HOME" or
            "LEFT" or "PAGEDOWN" or "PAGEUP" or "RIGHT" or "TAB" or "UP");
    }

    public async Task CreateDefaultShortcutsAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
    }

    public async Task ResetToDefaultShortcutsAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);

        try
        {
            var defaults = GetDefaultShortcuts();
            await Connection.RunInTransactionAsync(transaction =>
            {
                transaction.DeleteAll<KeyboardShortcut>();
                transaction.InsertAll(defaults, typeof(KeyboardShortcut));
            }).ConfigureAwait(false);
            await RefreshSnapshotCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        KeyboardShortcutsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task EnsureInitializedAsync()
    {
        if (!_isInitialized)
            await InitializeAsync().ConfigureAwait(false);
    }

    private async Task RepairAndSeedAsync()
    {
        await Connection.RunInTransactionAsync(transaction =>
        {
            var existing = transaction.Table<KeyboardShortcut>().ToList();
            var repaired = existing.Select(Clone).ToList();
            var changed = false;

            foreach (var shortcut in repaired)
            {
                var normalizedKey = NormalizeKey(shortcut.Key);
                if (!string.Equals(shortcut.Key, normalizedKey, StringComparison.Ordinal))
                {
                    shortcut.Key = normalizedKey;
                    changed = true;
                }
            }

            var mailShortcuts = repaired.Where(item => item.Mode == WinoApplicationMode.Mail).ToList();
            if (IsExactLegacyGeneratedSet(mailShortcuts))
            {
                repaired.RemoveAll(item => item.Mode == WinoApplicationMode.Mail);
                repaired.AddRange(GetDefaultShortcuts(WinoApplicationMode.Mail));
                changed = true;
            }

            foreach (var mode in SupportedModes)
            {
                if (repaired.Any(item => item.Mode == mode))
                    continue;

                repaired.AddRange(GetDefaultShortcuts(mode));
                changed = true;
            }

            var deduplicated = repaired
                .GroupBy(item => (item.Mode, item.Key, item.ModifierKeys))
                .Select(group => group
                    .OrderByDescending(item => item.IsEnabled)
                    .ThenBy(item => item.CreatedAt)
                    .ThenBy(item => item.Id)
                    .First())
                .ToList();

            changed |= deduplicated.Count != repaired.Count;

            if (changed)
            {
                transaction.DeleteAll<KeyboardShortcut>();
                transaction.InsertAll(deduplicated, typeof(KeyboardShortcut));
            }

            transaction.Execute(
                "CREATE UNIQUE INDEX IF NOT EXISTS IX_KeyboardShortcut_Mode_Key_Modifiers " +
                "ON KeyboardShortcut (Mode, Key COLLATE NOCASE, ModifierKeys)");
        }).ConfigureAwait(false);
    }

    private async Task RefreshSnapshotCoreAsync()
    {
        var enabled = await Connection.QueryAsync<KeyboardShortcut>(
            "SELECT * FROM KeyboardShortcut WHERE IsEnabled = ? ORDER BY Mode, Action",
            true).ConfigureAwait(false);
        var immutable = new ReadOnlyCollection<KeyboardShortcutSnapshot>(enabled
            .Select(shortcut => new KeyboardShortcutSnapshot(
                shortcut.Id,
                shortcut.Mode,
                shortcut.Key,
                shortcut.ModifierKeys,
                shortcut.Action,
                shortcut.CreatedAt))
            .ToList());
        Volatile.Write(ref _enabledShortcutsSnapshot, immutable);
    }

    private static bool IsActionAvailableInMode(WinoApplicationMode mode, KeyboardShortcutAction action)
        => mode switch
        {
            WinoApplicationMode.Mail => action is KeyboardShortcutAction.NewMail or
                KeyboardShortcutAction.ToggleReadUnread or KeyboardShortcutAction.ToggleFlag or
                KeyboardShortcutAction.ToggleArchive or KeyboardShortcutAction.Delete or
                KeyboardShortcutAction.Move or KeyboardShortcutAction.Reply or
                KeyboardShortcutAction.ReplyAll or KeyboardShortcutAction.Send,
            WinoApplicationMode.Calendar => action is KeyboardShortcutAction.NewEvent or KeyboardShortcutAction.Delete,
            WinoApplicationMode.Contacts => action is KeyboardShortcutAction.NewContact or KeyboardShortcutAction.Delete,
            WinoApplicationMode.Tasks => action is KeyboardShortcutAction.NewTask or KeyboardShortcutAction.Delete,
            _ => false
        };

    private static string NormalizeKey(string key)
    {
        var normalized = key?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized switch
        {
            "DEL" => "DELETE",
            "RETURN" => "ENTER",
            "ESC" => "ESCAPE",
            "SPACEBAR" => "SPACE",
            _ => normalized
        };
    }

    private static bool IsExactLegacyGeneratedSet(IReadOnlyCollection<KeyboardShortcut> shortcuts)
    {
        if (shortcuts.Count is not (8 or 9) || shortcuts.Any(shortcut => !shortcut.IsEnabled))
            return false;

        var legacy = new HashSet<(string Key, ModifierKeys Modifiers, KeyboardShortcutAction Action)>
        {
            ("DELETE", ModifierKeys.None, KeyboardShortcutAction.Delete),
            ("N", ModifierKeys.Control, KeyboardShortcutAction.NewMail),
            ("A", ModifierKeys.Control, KeyboardShortcutAction.ToggleArchive),
            ("R", ModifierKeys.Control, KeyboardShortcutAction.ToggleReadUnread),
            ("F", ModifierKeys.Control, KeyboardShortcutAction.ToggleFlag),
            ("M", ModifierKeys.Control, KeyboardShortcutAction.Move),
            ("R", ModifierKeys.Control | ModifierKeys.Shift, KeyboardShortcutAction.ReplyAll),
            ("ENTER", ModifierKeys.Control, KeyboardShortcutAction.Send)
        };

        if (shortcuts.Count == 9)
            legacy.Add(("R", ModifierKeys.Control, KeyboardShortcutAction.Reply));

        return shortcuts.All(item => legacy.Contains((NormalizeKey(item.Key), item.ModifierKeys, item.Action)));
    }

    private static List<KeyboardShortcut> GetDefaultShortcuts()
        => SupportedModes.SelectMany(GetDefaultShortcuts).ToList();

    private static IEnumerable<KeyboardShortcut> GetDefaultShortcuts(WinoApplicationMode mode)
        => mode switch
        {
            WinoApplicationMode.Mail =>
            [
                CreateDefault(mode, "N", ModifierKeys.Control, KeyboardShortcutAction.NewMail),
                CreateDefault(mode, "A", ModifierKeys.Control | ModifierKeys.Shift, KeyboardShortcutAction.ToggleArchive),
                CreateDefault(mode, "U", ModifierKeys.Control, KeyboardShortcutAction.ToggleReadUnread),
                CreateDefault(mode, "G", ModifierKeys.Control | ModifierKeys.Shift, KeyboardShortcutAction.ToggleFlag),
                CreateDefault(mode, "V", ModifierKeys.Control | ModifierKeys.Shift, KeyboardShortcutAction.Move),
                CreateDefault(mode, "R", ModifierKeys.Control, KeyboardShortcutAction.Reply),
                CreateDefault(mode, "R", ModifierKeys.Control | ModifierKeys.Shift, KeyboardShortcutAction.ReplyAll),
                CreateDefault(mode, "ENTER", ModifierKeys.Control, KeyboardShortcutAction.Send),
                CreateDefault(mode, "DELETE", ModifierKeys.None, KeyboardShortcutAction.Delete)
            ],
            WinoApplicationMode.Calendar =>
            [
                CreateDefault(mode, "N", ModifierKeys.Control, KeyboardShortcutAction.NewEvent),
                CreateDefault(mode, "DELETE", ModifierKeys.None, KeyboardShortcutAction.Delete)
            ],
            WinoApplicationMode.Contacts =>
            [
                CreateDefault(mode, "N", ModifierKeys.Control, KeyboardShortcutAction.NewContact),
                CreateDefault(mode, "DELETE", ModifierKeys.None, KeyboardShortcutAction.Delete)
            ],
            WinoApplicationMode.Tasks =>
            [
                CreateDefault(mode, "N", ModifierKeys.Control, KeyboardShortcutAction.NewTask),
                CreateDefault(mode, "DELETE", ModifierKeys.None, KeyboardShortcutAction.Delete)
            ],
            _ => []
        };

    private static KeyboardShortcut CreateDefault(
        WinoApplicationMode mode,
        string key,
        ModifierKeys modifierKeys,
        KeyboardShortcutAction action)
        => new()
        {
            Id = Guid.NewGuid(),
            Mode = mode,
            Key = key,
            ModifierKeys = modifierKeys,
            Action = action,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };

    private static KeyboardShortcut Clone(KeyboardShortcut shortcut)
        => new()
        {
            Id = shortcut.Id,
            Mode = shortcut.Mode,
            Key = shortcut.Key,
            ModifierKeys = shortcut.ModifierKeys,
            Action = shortcut.Action,
            IsEnabled = shortcut.IsEnabled,
            CreatedAt = shortcut.CreatedAt
        };

    private static KeyboardShortcut ToEntity(KeyboardShortcutSnapshot shortcut)
        => new()
        {
            Id = shortcut.Id,
            Mode = shortcut.Mode,
            Key = shortcut.Key,
            ModifierKeys = shortcut.ModifierKeys,
            Action = shortcut.Action,
            IsEnabled = true,
            CreatedAt = shortcut.CreatedAt
        };
}
