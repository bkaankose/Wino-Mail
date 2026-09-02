using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Mail.WinUI.Extensions;

namespace Wino.MenuFlyouts;

internal sealed class MailContextFlyoutShortcutResolver(IKeyboardShortcutService shortcutService)
{
    public ResolvedContextFlyoutShortcut? Resolve(MailOperation operation)
    {
        var action = MapAction(operation);
        if (action is null)
        {
            return null;
        }

        var shortcut = shortcutService.EnabledShortcutsSnapshot
            .Where(candidate => candidate.Mode == WinoApplicationMode.Mail && candidate.Action == action)
            .Where(candidate => KeyboardShortcutContextPolicy.CanExecute(
                candidate.Action,
                candidate.Key,
                candidate.ModifierKeys,
                KeyboardShortcutInputContext.List,
                false))
            .OrderBy(candidate => candidate.CreatedAt)
            .ThenBy(candidate => candidate.Id)
            .FirstOrDefault();

        if (shortcut is null)
        {
            return null;
        }

        KeyboardAccelerator? accelerator = null;
        var isSafeWhileFiltering = KeyboardShortcutContextPolicy.CanExecute(
            shortcut.Action,
            shortcut.Key,
            shortcut.ModifierKeys,
            KeyboardShortcutInputContext.List,
            true);

        if (isSafeWhileFiltering && Enum.TryParse(shortcut.Key, true, out VirtualKey key) && key != VirtualKey.None)
        {
            accelerator = new KeyboardAccelerator
            {
                Key = key,
                Modifiers = shortcut.ModifierKeys.ToVirtualKeyModifiers()
            };
        }

        return new ResolvedContextFlyoutShortcut(BuildDisplayText(shortcut), accelerator);
    }

    private static KeyboardShortcutAction? MapAction(MailOperation operation)
        => operation switch
        {
            MailOperation.Archive or MailOperation.UnArchive => KeyboardShortcutAction.ToggleArchive,
            MailOperation.SoftDelete or MailOperation.HardDelete => KeyboardShortcutAction.Delete,
            MailOperation.SetFlag or MailOperation.ClearFlag => KeyboardShortcutAction.ToggleFlag,
            MailOperation.MarkAsRead or MailOperation.MarkAsUnread => KeyboardShortcutAction.ToggleReadUnread,
            MailOperation.Reply => KeyboardShortcutAction.Reply,
            MailOperation.ReplyAll => KeyboardShortcutAction.ReplyAll,
            _ => null
        };

    private static string BuildDisplayText(KeyboardShortcutSnapshot shortcut)
    {
        var parts = new List<string>();

        if (shortcut.ModifierKeys.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (shortcut.ModifierKeys.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (shortcut.ModifierKeys.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (shortcut.ModifierKeys.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(shortcut.Key.Equals("DELETE", StringComparison.OrdinalIgnoreCase) ? "Delete" : shortcut.Key);

        return string.Join("+", parts);
    }
}

internal sealed record ResolvedContextFlyoutShortcut(string DisplayText, KeyboardAccelerator? Accelerator);
