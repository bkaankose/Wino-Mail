using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Playground.ViewModels;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class ContactPicturePage : Page
{
    public ContactPicturePageViewModel ViewModel { get; } = new();

    public ContactPicturePage()
    {
        InitializeComponent();
    }
}
