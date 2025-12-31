using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class SettingOptionsPage : SettingOptionsPageAbstract
{
    public SettingOptionsPage()
    {
        InitializeComponent();
    }

    private void SettingOptionClicked(object sender, RoutedEventArgs e)
    {
        WinoPage? page = sender switch
        {
            Button button when button.Tag is WinoPage p => p,
            _ => null
        };

        if (page.HasValue)
        {
            ViewModel.NavigateSubDetailCommand.Execute(page.Value);
        }
    }
}
