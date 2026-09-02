using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Mail.Controls.Core.ContextFlyout;

namespace Wino.MenuFlyouts;

internal sealed class MailContextFlyoutShortcutResolver(IKeyboardShortcutService shortcutService)
{
    public ContextFlyoutShortcut? Resolve(MailOperation operation)
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

        // A shortcut the list cannot run while a text field has focus is still shown, but it is not
        // offered as a key: an empty key stops the flyout from registering an accelerator for it.
        var isSafeWhileFiltering = KeyboardShortcutContextPolicy.CanExecute(
            shortcut.Action,
            shortcut.Key,
            shortcut.ModifierKeys,
            KeyboardShortcutInputContext.List,
            true);

        return new ContextFlyoutShortcut(
            BuildDisplayText(shortcut),
            isSafeWhileFiltering ? shortcut.Key : string.Empty,
            shortcut.ModifierKeys.HasFlag(ModifierKeys.Control),
            shortcut.ModifierKeys.HasFlag(ModifierKeys.Alt),
            shortcut.ModifierKeys.HasFlag(ModifierKeys.Shift),
            shortcut.ModifierKeys.HasFlag(ModifierKeys.Windows));
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
