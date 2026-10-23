using Microsoft.UI.Xaml.Controls;
using MailCal.Mail.Controls.Playground.ViewModels;

namespace MailCal.Mail.Controls.Playground.Pages;

public sealed partial class ContactPicturePage : Page
{
    public ContactPicturePageViewModel ViewModel { get; } = new();

    public ContactPicturePage()
    {
        InitializeComponent();
    }
}
