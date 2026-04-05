using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Core.ViewModels.Data;

public class KeyboardShortcutActionViewModel
{
    public WinoApplicationMode Mode { get; }
    public KeyboardShortcutAction Action { get; }

    public string DisplayName => Action switch
    {
        KeyboardShortcutAction.NewMail => Translator.MenuNewMail,
        KeyboardShortcutAction.ToggleReadUnread => Translator.KeyboardShortcuts_ActionToggleReadUnread,
        KeyboardShortcutAction.ToggleFlag => Translator.KeyboardShortcuts_ActionToggleFlag,
        KeyboardShortcutAction.ToggleArchive => Translator.KeyboardShortcuts_ActionToggleArchive,
        KeyboardShortcutAction.Delete => Translator.Buttons_Delete,
        KeyboardShortcutAction.Move => Translator.MailOperation_Move,
        KeyboardShortcutAction.Reply => Translator.MailOperation_Reply,
        KeyboardShortcutAction.ReplyAll => Translator.MailOperation_ReplyAll,
        KeyboardShortcutAction.Send => Translator.Buttons_Send,
        KeyboardShortcutAction.NewEvent => Translator.CalendarEventCompose_NewEventButton,
        _ => Action.ToString()
    };

    public KeyboardShortcutActionViewModel(WinoApplicationMode mode, KeyboardShortcutAction action)
    {
        Mode = mode;
        Action = action;
    }

    public override string ToString() => DisplayName;
}
