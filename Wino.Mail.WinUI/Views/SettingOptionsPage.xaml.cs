using CommunityToolkit.WinUI.Controls;
using Wino.Core.Domain.Enums;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class SettingOptionsPage : SettingOptionsPageAbstract
{
    public SettingOptionsPage()
    {
        InitializeComponent();
    }

    private void SettingOptionClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is SettingsCard card && card.CommandParameter is WinoPage page) ViewModel.NavigateSubDetailCommand.Execute(page);
    }
}
