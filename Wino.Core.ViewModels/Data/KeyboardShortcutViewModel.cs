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
    private bool isEnabled;

    public Guid Id { get; }
    public string Key { get; }
    public ModifierKeys ModifierKeys { get; }
    public MailOperation MailOperation { get; }
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

    public string MailOperationDisplayName
    {
        get
        {
            return MailOperation switch
            {
                MailOperation.Archive => Translator.MailOperation_Archive,
                MailOperation.UnArchive => Translator.MailOperation_Unarchive,
                MailOperation.SoftDelete => Translator.MailOperation_Delete,
                MailOperation.Move => Translator.MailOperation_Move,
                MailOperation.MoveToJunk => Translator.MailOperation_MoveJunk,
                MailOperation.SetFlag => Translator.MailOperation_SetFlag,
                MailOperation.ClearFlag => Translator.MailOperation_ClearFlag,
                MailOperation.MarkAsRead => Translator.MailOperation_MarkAsRead,
                MailOperation.MarkAsUnread => Translator.MailOperation_MarkAsUnread,
                MailOperation.Reply => Translator.MailOperation_Reply,
                MailOperation.ReplyAll => Translator.MailOperation_ReplyAll,
                MailOperation.Forward => Translator.MailOperation_Forward,
                _ => MailOperation.ToString()
            };
        }
    }

    public KeyboardShortcutViewModel(KeyboardShortcut shortcut)
    {
        Id = shortcut.Id;
        Key = shortcut.Key;
        ModifierKeys = shortcut.ModifierKeys;
        MailOperation = shortcut.MailOperation;
        CreatedAt = shortcut.CreatedAt;
        IsEnabled = shortcut.IsEnabled;
    }

    public KeyboardShortcut ToEntity()
    {
        return new KeyboardShortcut
        {
            Id = Id,
            Key = Key,
            ModifierKeys = ModifierKeys,
            MailOperation = MailOperation,
            CreatedAt = CreatedAt,
            IsEnabled = IsEnabled
        };
    }
}
