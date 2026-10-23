using Microsoft.UI.Xaml.Controls;
using MailCal.Core.ViewModels.Data;
using MailCal.Mail.WinUI.Views.Abstract;
using WinRT;

namespace MailCal.Views.Settings;

public sealed partial class KeyboardShortcutsPage : KeyboardShortcutsPageAbstract
{
    public KeyboardShortcutsPage()
    {
        this.InitializeComponent();
    }

    [DynamicWindowsRuntimeCast(typeof(Button))]
    private void EditShortcut_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is KeyboardShortcutViewModel shortcut)
        {
            ViewModel.StartEditingShortcutCommand.Execute(shortcut);
        }
    }

    [DynamicWindowsRuntimeCast(typeof(Button))]
    private void DeleteShortcut_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is KeyboardShortcutViewModel shortcut)
        {
            ViewModel.DeleteShortcutCommand.Execute(shortcut);
        }
    }

    private void ShortcutEnabled_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { IsLoaded: true, Tag: KeyboardShortcutViewModel shortcut } toggleSwitch)
        {
            shortcut.IsEnabled = toggleSwitch.IsOn;
            ViewModel.ToggleShortcutCommand.Execute(shortcut);
        }
    }
}
