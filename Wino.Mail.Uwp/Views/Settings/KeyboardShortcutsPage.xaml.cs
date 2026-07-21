using Windows.UI.Xaml.Controls;
using Wino.Core.ViewModels.Data;
using Wino.Mail.Uwp.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class KeyboardShortcutsPage : KeyboardShortcutsPageAbstract
{
    public KeyboardShortcutsPage()
    {
        this.InitializeComponent();
    }

    private void EditShortcut_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is KeyboardShortcutViewModel shortcut)
        {
            ViewModel.StartEditingShortcutCommand.Execute(shortcut);
        }
    }

    private void DeleteShortcut_Click(object sender, Windows.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Button button && button.CommandParameter is KeyboardShortcutViewModel shortcut)
        {
            ViewModel.DeleteShortcutCommand.Execute(shortcut);
        }
    }
}
