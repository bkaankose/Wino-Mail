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
            "accountIcon" => typeof(AccountIconPage),
            "contact" => typeof(ContactPicturePage),
            "mailList" => typeof(MailListPage),
            "editor" => typeof(EditorPage),
            "searchBar" => typeof(SearchBarPage),
            "intelligenceHeader" => typeof(IntelligenceHeaderPage),
            "intelligenceProgress" => typeof(IntelligenceProgressPage),
            "synchronizationButton" => typeof(SynchronizationButtonPage),
            _ => typeof(AccountIconPage),
        };

        if (ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page);
        }
    }
}
