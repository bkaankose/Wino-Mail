using Microsoft.UI.Xaml;
using Wino.Helpers;
using Wino.Views.Abstract;

namespace Wino.Views.Settings;

public sealed partial class MailNotificationSettingsPage : MailNotificationSettingsPageAbstract
{
    public MailNotificationSettingsPage()
    {
        InitializeComponent();
    }

    private void PlayNotificationSoundButton_Click(object sender, RoutedEventArgs e)
        => NotificationSoundPlayer.Play(ViewModel.SelectedNotificationSoundEvent);
}
