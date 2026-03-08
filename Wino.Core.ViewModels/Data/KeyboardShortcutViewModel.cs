using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.ViewModels.Data;

/// <summary>
/// ViewModel wrapper for KeyboardShortcut entity.
/// </summary>
public partial class KeyboardShortcutViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    public Guid Id { get; }
    public WinoApplicationMode Mode { get; }
    public string Key { get; }
    public ModifierKeys ModifierKeys { get; }
    public KeyboardShortcutAction Action { get; }
    public DateTime CreatedAt { get; }

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

    public string ModeDisplayName => Mode switch
    {
        WinoApplicationMode.Mail => Translator.KeyboardShortcuts_ModeMail,
        WinoApplicationMode.Calendar => Translator.KeyboardShortcuts_ModeCalendar,
        _ => Mode.ToString()
    };

    public string ActionDisplayName
    {
        get
        {
            return Action switch
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
        }
    }

    public KeyboardShortcutViewModel(KeyboardShortcut shortcut)
    {
        Id = shortcut.Id;
        Mode = shortcut.Mode;
        Key = shortcut.Key;
        ModifierKeys = shortcut.ModifierKeys;
        Action = shortcut.Action;
        CreatedAt = shortcut.CreatedAt;
        IsEnabled = shortcut.IsEnabled;
    }

    public KeyboardShortcut ToEntity()
    {
        return new KeyboardShortcut
        {
            Id = Id,
            Mode = Mode,
            Key = Key,
            ModifierKeys = ModifierKeys,
            Action = Action,
            CreatedAt = CreatedAt,
            IsEnabled = IsEnabled
        };
    }
}
