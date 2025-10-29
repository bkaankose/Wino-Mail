using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Wino.Core.ViewModels.Data;

namespace Wino.Dialogs;

public sealed partial class KeyboardShortcutDialog : ContentDialog
{
    public KeyboardShortcutDialogResult Result { get; private set; } = KeyboardShortcutDialogResult.Canceled();

    public List<MailOperationViewModel> AvailableMailOperations { get; }

    public MailOperationViewModel SelectedMailOperation { get; set; }
    public bool IsControlPressed { get; set; }
    public bool IsAltPressed { get; set; }
    public bool IsShiftPressed { get; set; }
    public bool IsWindowsPressed { get; set; }

    public KeyboardShortcutDialog()
    {
        InitializeComponent();
        AvailableMailOperations = GetAvailableMailOperations();
        SelectedMailOperation = AvailableMailOperations.FirstOrDefault();
    }

    public KeyboardShortcutDialog(KeyboardShortcut existingShortcut) : this()
    {
        if (existingShortcut != null)
        {
            KeyInputTextBox.Text = existingShortcut.Key;
            SelectedMailOperation = AvailableMailOperations.FirstOrDefault(x => x.Operation == existingShortcut.MailOperation);

            var modifiers = existingShortcut.ModifierKeys;
            IsControlPressed = modifiers.HasFlag(ModifierKeys.Control);
            IsAltPressed = modifiers.HasFlag(ModifierKeys.Alt);
            IsShiftPressed = modifiers.HasFlag(ModifierKeys.Shift);
            IsWindowsPressed = modifiers.HasFlag(ModifierKeys.Windows);

            Title = "Edit Keyboard Shortcut";
        }
    }

    private void SaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Clear any previous error
        ErrorTextBlock.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

        // Validate input
        if (string.IsNullOrWhiteSpace(KeyInputTextBox.Text))
        {
            ShowError("Please enter a key for the shortcut.");
            args.Cancel = true;
            return;
        }

        if (SelectedMailOperation == null || SelectedMailOperation.Operation == MailOperation.None)
        {
            ShowError("Please select a mail operation for the shortcut.");
            args.Cancel = true;
            return;
        }

        // Get modifier keys
        var modifierKeys = GetSelectedModifierKeys();

        // Create successful result
        Result = KeyboardShortcutDialogResult.Success(KeyInputTextBox.Text, modifierKeys, SelectedMailOperation.Operation);
    }

    private void KeyInputTextBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Clear error when user starts typing
        ErrorTextBlock.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

        var key = e.Key.ToString();

        // Update modifier states based on current key press
        IsControlPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        IsAltPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        IsShiftPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        IsWindowsPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.LeftWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) ||
                          Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.RightWindows).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        // Set the key (ignore modifier keys themselves)
        if (key != "Control" && key != "Menu" && key != "Shift" && key != "LeftWindows" && key != "RightWindows")
        {
            KeyInputTextBox.Text = key;
        }

        // Prevent the key from being processed further
        // e.Handled = true;
    }

    private ModifierKeys GetSelectedModifierKeys()
    {
        var modifiers = ModifierKeys.None;

        if (IsControlPressed) modifiers |= ModifierKeys.Control;
        if (IsAltPressed) modifiers |= ModifierKeys.Alt;
        if (IsShiftPressed) modifiers |= ModifierKeys.Shift;
        if (IsWindowsPressed) modifiers |= ModifierKeys.Windows;

        return modifiers;
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorTextBlock.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }

    private static List<MailOperationViewModel> GetAvailableMailOperations()
    {
        var operations = new List<MailOperationViewModel>();

        // Add commonly used mail operations that make sense for keyboard shortcuts
        var validOperations = new[]
        {
            MailOperation.Archive,
            MailOperation.UnArchive,
            MailOperation.SoftDelete,
            MailOperation.Move,
            MailOperation.MoveToJunk,
            MailOperation.SetFlag,
            MailOperation.ClearFlag,
            MailOperation.MarkAsRead,
            MailOperation.MarkAsUnread,
            MailOperation.Reply,
            MailOperation.ReplyAll,
            MailOperation.Forward
        };

        foreach (var operation in validOperations)
        {
            operations.Add(new MailOperationViewModel(operation));
        }

        return operations.OrderBy(x => x.DisplayName).ToList();
    }
}
