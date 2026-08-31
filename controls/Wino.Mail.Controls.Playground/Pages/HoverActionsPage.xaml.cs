using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class HoverActionsPage : Page
{
    public HoverActionsPage()
    {
        InitializeComponent();
    }

    public HoverActionsPageViewModel ViewModel { get; } = new();

    private void SystemThemeClicked(object sender, RoutedEventArgs e) => PreviewRoot.RequestedTheme = ElementTheme.Default;

    private void LightThemeClicked(object sender, RoutedEventArgs e) => PreviewRoot.RequestedTheme = ElementTheme.Light;

    private void DarkThemeClicked(object sender, RoutedEventArgs e) => PreviewRoot.RequestedTheme = ElementTheme.Dark;
}
