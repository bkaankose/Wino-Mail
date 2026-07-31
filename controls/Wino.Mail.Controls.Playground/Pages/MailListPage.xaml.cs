using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Playground.ViewModels;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class MailListPage : Page
{
    public MailListPageViewModel ViewModel { get; } = new();

    public MailListPage()
    {
        InitializeComponent();
    }
}
