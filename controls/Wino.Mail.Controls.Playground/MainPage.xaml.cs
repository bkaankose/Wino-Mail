using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.AppModeSwitcher;
using Wino.Mail.Controls.Playground.Pages;

namespace Wino.Mail.Controls.Playground;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
        Navigation.SelectedItem = Navigation.MenuItems[0];
        UpdateFooterOrientation();
    }

    private void DarkModeToggled(object sender, RoutedEventArgs e)
        => RequestedTheme = DarkModeToggle.IsOn ? ElementTheme.Dark : ElementTheme.Light;

    private void NavigationPaneOpening(NavigationView sender, object args) => UpdateFooterOrientation();

    private void NavigationPaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
        => UpdateFooterOrientation();

    private void NavigationDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
        => UpdateFooterOrientation();

    private void FooterModeInvoked(object? sender, WinoAppModeInvokedEventArgs e)
    {
        if (ContentFrame.Content is AppModeSwitcherPage page)
            page.SelectModeFromFooter(e.Index);
    }

    private void FooterSettingsInvoked(object? sender, EventArgs e)
    {
        if (ContentFrame.Content is AppModeSwitcherPage page)
            page.SelectSettingsFromFooter();
    }

    private void UpdateFooterOrientation()
    {
        if (FooterSwitcher is null)
            return;

        FooterSwitcher.Orientation = Navigation.IsPaneOpen ? Orientation.Horizontal : Orientation.Vertical;
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
            "appModeSwitcher" => typeof(AppModeSwitcherPage),
            _ => typeof(AccountIconPage),
        };

        if (ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page);
        }
    }
}
