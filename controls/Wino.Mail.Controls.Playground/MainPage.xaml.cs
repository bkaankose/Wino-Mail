using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Playground.Pages;

namespace Wino.Mail.Controls.Playground;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    private void NavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var page = (args.SelectedItem as NavigationViewItem)?.Tag switch
        {
            "contact" => typeof(ContactPicturePage),
            "mailList" => typeof(MailListPage),
            "editor" => typeof(EditorPage),
            _ => typeof(ContactPicturePage),
        };

        if (ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page);
        }
    }
}
