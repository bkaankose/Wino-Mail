using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.WinUI.Views.Abstract;

namespace Wino.Views;

public sealed partial class SpecialImapCredentialsPage : SpecialImapCredentialsPageAbstract
{
    public SpecialImapCredentialsPage()
    {
        InitializeComponent();
    }

    private void CalendarModeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is ListView lv)
            ViewModel.SelectedCalendarModeIndex = lv.SelectedIndex;
    }

    private void AppPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
            ViewModel.AppSpecificPassword = pb.Password;
    }
}
