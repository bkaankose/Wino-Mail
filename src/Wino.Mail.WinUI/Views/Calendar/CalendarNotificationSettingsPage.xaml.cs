using Microsoft.UI.Xaml;
using Wino.Helpers;

namespace Wino.Mail.WinUI.Views.Calendar;

public sealed partial class CalendarNotificationSettingsPage : Wino.Mail.WinUI.Views.Abstract.CalendarNotificationSettingsPageAbstract
{
    public CalendarNotificationSettingsPage()
    {
        InitializeComponent();
    }

    private void PlayNotificationSoundButton_Click(object sender, RoutedEventArgs e)
        => NotificationSoundPlayer.Play(ViewModel.SelectedNotificationSoundEvent);
}
