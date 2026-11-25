using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Folders;
using Wino.Mail.WinUI.Helpers;
using Wino.Mail.WinUI.Views.Abstract;
using WinRT;

namespace Wino.Mail.WinUI.Views.Account;

public sealed partial class AccountMailSettingsPage : AccountMailSettingsPageAbstract
{
    public AccountMailSettingsPage()
    {
        InitializeComponent();
    }

    [DynamicWindowsRuntimeCast(typeof(CheckBox))]
    private async void SyncFolderToggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is IMailItemFolder folder)
            await ViewModel.FolderSyncToggledAsync(folder, checkBox.IsChecked.GetValueOrDefault());
    }

    [DynamicWindowsRuntimeCast(typeof(CheckBox))]
    private async void JumpListCheckboxToggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is IMailItemFolder folder)
            await ViewModel.FolderJumpListToggledAsync(folder, checkBox.IsChecked.GetValueOrDefault());
    }

    [DynamicWindowsRuntimeCast(typeof(CheckBox))]
    private void JumpListCheckBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
            checkBox.IsEnabled = ViewModel.IsJumpListEnabled;
    }

    [DynamicWindowsRuntimeCast(typeof(ToggleSwitch))]
    private void JumpListMasterToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggleSwitch)
            return;

        foreach (var checkBox in this.FindDescendants<CheckBox>())
        {
            if (checkBox.Name == "FolderJumpListCheckBox")
                checkBox.IsEnabled = toggleSwitch.IsOn;
        }
    }
}
