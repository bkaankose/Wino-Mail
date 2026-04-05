using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain;
using Wino.Core.Domain.Models;
using Wino.Core.ViewModels.Data;

namespace Wino.Dialogs;

public sealed partial class KeyboardShortcutDialog : ContentDialog
{
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

    private ModifierKeys _modifierKeys;
    private string _key = string.Empty;

    public KeyboardShortcutDialog()
    {
        InitializeComponent();
        RefreshAvailableActions();
    }

    public KeyboardShortcutDialog(KeyboardShortcut existingShortcut) : this()
    {
        if (existingShortcut != null)
        {
            SelectedMode = existingShortcut.Mode;
            _modifierKeys = existingShortcut.ModifierKeys;
            _key = existingShortcut.Key;
            RefreshAvailableActions(existingShortcut.Action);
            KeyInputTextBox.Text = BuildDisplayString(_key, _modifierKeys);
            Title = Translator.KeyboardShortcuts_EditTitle;
        }
    }

    private void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Clear any previous error
        ErrorBorder.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

        // Validate input
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

        Result = KeyboardShortcutDialogResult.Success(SelectedMode, _key, _modifierKeys, SelectedAction.Action);
    }

    private void KeyInputTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        ErrorBorder.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

        _modifierKeys = GetCurrentModifierKeys();
        var key = NormalizeKey(e.Key);

        if (!string.IsNullOrEmpty(key))
        {
            _key = key;
        }

        KeyInputTextBox.Text = string.IsNullOrEmpty(_key)
            ? BuildDisplayString(string.Empty, _modifierKeys)
            : BuildDisplayString(_key, _modifierKeys);

        e.Handled = true;
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
            _ => []
        };

        return actions
            .Select(action => new KeyboardShortcutActionViewModel(mode, action))
            .ToList();
    }

    private static ModifierKeys GetCurrentModifierKeys()
    {
        var modifiers = ModifierKeys.None;

        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            modifiers |= ModifierKeys.Control;

        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            modifiers |= ModifierKeys.Alt;

        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            modifiers |= ModifierKeys.Shift;

        if (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
            Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.RightWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            modifiers |= ModifierKeys.Windows;
        }

        return modifiers;
    }

    private static string NormalizeKey(Windows.System.VirtualKey key)
    {
        return key switch
        {
            Windows.System.VirtualKey.Control or
            Windows.System.VirtualKey.LeftControl or
            Windows.System.VirtualKey.RightControl or
            Windows.System.VirtualKey.Menu or
            Windows.System.VirtualKey.LeftMenu or
            Windows.System.VirtualKey.RightMenu or
            Windows.System.VirtualKey.Shift or
            Windows.System.VirtualKey.LeftShift or
            Windows.System.VirtualKey.RightShift or
            Windows.System.VirtualKey.LeftWindows or
            Windows.System.VirtualKey.RightWindows => string.Empty,
            _ => key.ToString()
        };
    }

    private static string BuildDisplayString(string key, ModifierKeys modifierKeys)
    {
        var parts = new List<string>();

        if (modifierKeys.HasFlag(ModifierKeys.Control))
            parts.Add("Ctrl");
        if (modifierKeys.HasFlag(ModifierKeys.Alt))
            parts.Add("Alt");
        if (modifierKeys.HasFlag(ModifierKeys.Shift))
            parts.Add("Shift");
        if (modifierKeys.HasFlag(ModifierKeys.Windows))
            parts.Add("Win");
        if (!string.IsNullOrEmpty(key))
            parts.Add(key);

        return string.Join("+", parts);
    }
}
