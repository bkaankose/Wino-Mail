using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.Controls.Core.HoverActions;
using Wino.Mail.Controls.HoverActions;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class HoverActionsPage : Page
{
    public HoverActionsPage()
    {
        InitializeComponent();
    }

    public HoverActionsPageViewModel ViewModel { get; } = new();

    private void PositionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PositionCombo.SelectedItem is ComboBoxItem { Tag: HoverActionPosition position })
        {
            PositionPreview.Position = position;
        }
    }

    private void ButtonSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ButtonSizeCombo.SelectedItem is ComboBoxItem { Tag: HoverActionButtonSize buttonSize })
        {
            PositionPreview.ButtonSize = buttonSize;
        }
    }

    private void SystemThemeClicked(object sender, RoutedEventArgs e) => PreviewRoot.RequestedTheme = ElementTheme.Default;

    private void LightThemeClicked(object sender, RoutedEventArgs e) => PreviewRoot.RequestedTheme = ElementTheme.Light;

    private void DarkThemeClicked(object sender, RoutedEventArgs e) => PreviewRoot.RequestedTheme = ElementTheme.Dark;
}
