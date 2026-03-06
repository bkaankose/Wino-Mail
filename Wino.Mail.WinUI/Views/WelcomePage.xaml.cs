using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using CommunityToolkit.WinUI.Controls;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class WelcomePage : WelcomePageAbstract
{
    public WelcomePage()
    {
        InitializeComponent();
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not Segmented segmented)
            return;

        bool isFeaturesTab = segmented.SelectedIndex == 0;

        FeaturesFlipView.Visibility = isFeaturesTab ? Visibility.Visible : Visibility.Collapsed;
        WhatsNewFlipView.Visibility = isFeaturesTab ? Visibility.Collapsed : Visibility.Visible;
    }
}
