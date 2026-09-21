using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Entities.Mail;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class JunkEmailSettingsPage : JunkEmailSettingsPageAbstract
{
    public JunkEmailSettingsPage()
    {
        InitializeComponent();
    }

    private async void RemoveSenderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is JunkSender junkSender)
        {
            await ViewModel.RemoveSenderAsync(junkSender);
        }
    }
}
