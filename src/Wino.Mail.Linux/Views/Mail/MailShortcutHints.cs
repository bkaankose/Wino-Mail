using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models;
using Wino.Mail.Linux.Infrastructure;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>The shortcut hints of the mail context menu: the port of Windows <c>ContextFlyoutShortcutResolver</c>. Display only.</summary>
internal static class MailShortcutHints
{
    /// <summary>The GTK accelerator of the first enabled Mail-mode shortcut for <paramref name="operation"/>, or null.</summary>
    public static string? Accelerator(IKeyboardShortcutService shortcuts, MailOperation operation)
    {
        if (Action(operation) is not { } action) return null;
        KeyboardShortcutSnapshot? best = null;
        foreach (var candidate in shortcuts.EnabledShortcutsSnapshot)
        {
            if (candidate.Mode != WinoApplicationMode.Mail || candidate.Action != action || candidate.ModifierKeys.HasFlag(ModifierKeys.Command)) continue;
            if (!KeyboardShortcutContextPolicy.CanExecute(candidate.Action, candidate.Key, candidate.ModifierKeys, KeyboardShortcutInputContext.List, false)) continue;
            if (best is null || candidate.CreatedAt < best.CreatedAt || (candidate.CreatedAt == best.CreatedAt && candidate.Id.CompareTo(best.Id) < 0)) best = candidate;
        }
        return best is null ? null : ShortcutKeys.Accelerator(best.ModifierKeys, best.Key);
    }

    private static KeyboardShortcutAction? Action(MailOperation operation) => operation switch
    {
        MailOperation.Archive or MailOperation.UnArchive => KeyboardShortcutAction.ToggleArchive,
        MailOperation.SoftDelete or MailOperation.HardDelete => KeyboardShortcutAction.Delete,
        MailOperation.SetFlag or MailOperation.ClearFlag => KeyboardShortcutAction.ToggleFlag,
        MailOperation.MarkAsRead or MailOperation.MarkAsUnread => KeyboardShortcutAction.ToggleReadUnread,
        MailOperation.Reply => KeyboardShortcutAction.Reply,
        MailOperation.ReplyAll => KeyboardShortcutAction.ReplyAll,
        MailOperation.Forward => KeyboardShortcutAction.Forward,
        _ => null,
    };
}
