using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Windows.System;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain;
using Wino.Core.Domain.Models;
using Wino.Core.ViewModels.Data;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.WinUI;
using Wino.Mail.Controls.HotKeyInput;

namespace Wino.Dialogs;

public sealed partial class KeyboardShortcutDialog : ContentDialog
{
    private readonly IKeyboardShortcutService _keyboardShortcutService = WinoApplication.Current.Services.GetRequiredService<IKeyboardShortcutService>();
    private readonly IWinoLogger _logger = WinoApplication.Current.Services.GetRequiredService<IWinoLogger>();
    public KeyboardShortcutDialogResult Result { get; private set; } = KeyboardShortcutDialogResult.Canceled();

    public List<KeyboardShortcutActionViewModel> AvailableActions { get; private set; } = [];

    public KeyboardShortcutActionViewModel SelectedAction { get; set; } = null!;
    public WinoApplicationMode SelectedMode { get; set; } = WinoApplicationMode.Mail;
    public bool IsMailModeSelected
    {
        get => SelectedMode == WinoApplicationMode.Mail;
        set
        {
            if (!value || SelectedMode == WinoApplicationMode.Mail) return;
            SelectedMode = WinoApplicationMode.Mail;
            RefreshAvailableActions();
        }
    }

    public bool IsCalendarModeSelected
    {
        get => SelectedMode == WinoApplicationMode.Calendar;
        set
        {
            if (!value || SelectedMode == WinoApplicationMode.Calendar) return;
            SelectedMode = WinoApplicationMode.Calendar;
            RefreshAvailableActions();
        }
    }

    public bool IsContactsModeSelected
    {
        get => SelectedMode == WinoApplicationMode.Contacts;
        set
        {
            if (!value || SelectedMode == WinoApplicationMode.Contacts) return;
            SelectedMode = WinoApplicationMode.Contacts;
            RefreshAvailableActions();
        }
    }

    public bool IsTasksModeSelected
    {
        get => SelectedMode == WinoApplicationMode.Tasks;
        set
        {
            if (!value || SelectedMode == WinoApplicationMode.Tasks) return;
            SelectedMode = WinoApplicationMode.Tasks;
            RefreshAvailableActions();
        }
    }

    private ModifierKeys _modifierKeys;
    private string _key = string.Empty;
    private Guid? _existingShortcutId;

    public KeyboardShortcutDialog()
    {
        InitializeComponent();
        RefreshAvailableActions();
    }

    public KeyboardShortcutDialog(KeyboardShortcut existingShortcut) : this()
    {
        if (existingShortcut != null)
        {
            _existingShortcutId = existingShortcut.Id;
            SelectedMode = existingShortcut.Mode;
            _modifierKeys = existingShortcut.ModifierKeys;
            _key = existingShortcut.Key;
            RefreshAvailableActions(existingShortcut.Action);
            KeyInputTextBox.Key = Enum.TryParse(_key, true, out VirtualKey key) ? key : VirtualKey.None;
            KeyInputTextBox.Modifiers = ToVirtualModifiers(_modifierKeys);
            Title = Translator.KeyboardShortcuts_EditTitle;
        }
    }

    private async void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();

        try
        {
            ErrorBorder.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(_key))
            {
                ShowError(Translator.KeyboardShortcuts_EnterKey);
                args.Cancel = true;
                return;
            }

            if (SelectedAction == null || SelectedAction.Action == KeyboardShortcutAction.None)
            {
                ShowError(Translator.KeyboardShortcuts_SelectOperation);
                args.Cancel = true;
                return;
            }

            var candidate = new KeyboardShortcut
            {
                Mode = SelectedMode,
                Key = _key,
                ModifierKeys = _modifierKeys,
                Action = SelectedAction.Action
            };
            if (!_keyboardShortcutService.IsShortcutAllowed(candidate))
            {
                ShowError(Translator.KeyboardShortcuts_InvalidShortcut);
                args.Cancel = true;
                return;
            }

            if (await _keyboardShortcutService.IsKeyCombinationInUseAsync(
                    SelectedMode,
                    _key,
                    _modifierKeys,
                    _existingShortcutId))
            {
                ShowError(Translator.KeyboardShortcuts_ShortcutInUse);
                args.Cancel = true;
                return;
            }

            Result = KeyboardShortcutDialogResult.Success(SelectedMode, _key, _modifierKeys, SelectedAction.Action);
        }
        catch (Exception exception)
        {
            _logger.CaptureException(exception, "KeyboardShortcutDialog.Validate");
            ShowError(Translator.KeyboardShortcuts_FailedToSave);
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void KeyInput_HotKeyCommitted(object? sender, HotKeyCommittedEventArgs e)
    {
        ErrorBorder.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        _modifierKeys = ToDomainModifiers(e.Modifiers);
        _key = e.Key.ToString();
        KeyInputTextBox.Key = e.Key;
        KeyInputTextBox.Modifiers = e.Modifiers;
    }

    private void RefreshAvailableActions(KeyboardShortcutAction selectedAction = KeyboardShortcutAction.None)
    {
        AvailableActions = GetAvailableActions(SelectedMode);
        SelectedAction = AvailableActions.FirstOrDefault(x => x.Action == selectedAction) ?? AvailableActions.FirstOrDefault()!;
        Bindings.Update();
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorBorder.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }

    private static List<KeyboardShortcutActionViewModel> GetAvailableActions(WinoApplicationMode mode)
    {
        KeyboardShortcutAction[] actions = mode switch
        {
            WinoApplicationMode.Mail =>
            [
                KeyboardShortcutAction.NewMail,
                KeyboardShortcutAction.ToggleReadUnread,
                KeyboardShortcutAction.ToggleFlag,
                KeyboardShortcutAction.ToggleArchive,
                KeyboardShortcutAction.Delete,
                KeyboardShortcutAction.Move,
                KeyboardShortcutAction.Reply,
                KeyboardShortcutAction.ReplyAll,
                KeyboardShortcutAction.Send
            ],
            WinoApplicationMode.Calendar =>
            [
                KeyboardShortcutAction.NewEvent,
                KeyboardShortcutAction.Delete
            ],
            WinoApplicationMode.Contacts =>
            [
                KeyboardShortcutAction.NewContact,
                KeyboardShortcutAction.Delete
            ],
            WinoApplicationMode.Tasks =>
            [
                KeyboardShortcutAction.NewTask,
                KeyboardShortcutAction.Delete
            ],
            _ => []
        };

        return actions
            .Select(action => new KeyboardShortcutActionViewModel(mode, action))
            .ToList();
    }

    private static ModifierKeys ToDomainModifiers(VirtualKeyModifiers modifiers)
    {
        var result = ModifierKeys.None;
        if (modifiers.HasFlag(VirtualKeyModifiers.Control))
            result |= ModifierKeys.Control;
        if (modifiers.HasFlag(VirtualKeyModifiers.Menu))
            result |= ModifierKeys.Alt;
        if (modifiers.HasFlag(VirtualKeyModifiers.Shift))
            result |= ModifierKeys.Shift;
        if (modifiers.HasFlag(VirtualKeyModifiers.Windows))
            result |= ModifierKeys.Windows;
        return result;
    }

    private static VirtualKeyModifiers ToVirtualModifiers(ModifierKeys modifiers)
    {
        var result = VirtualKeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
            result |= VirtualKeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt))
            result |= VirtualKeyModifiers.Menu;
        if (modifiers.HasFlag(ModifierKeys.Shift))
            result |= VirtualKeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows))
            result |= VirtualKeyModifiers.Windows;
        return result;
    }
}
