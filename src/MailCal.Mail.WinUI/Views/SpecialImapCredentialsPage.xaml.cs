using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MailCal.Mail.WinUI.Views.Abstract;
using WinRT;

namespace MailCal.Views;

public sealed partial class SpecialImapCredentialsPage : SpecialImapCredentialsPageAbstract
{
    public SpecialImapCredentialsPage()
    {
        InitializeComponent();
    }

    [DynamicWindowsRuntimeCast(typeof(PasswordBox))]
    private void AppPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pb)
            ViewModel.AppSpecificPassword = pb.Password;
    }
}
