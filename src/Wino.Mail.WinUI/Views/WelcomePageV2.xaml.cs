using Microsoft.UI.Xaml.Controls;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class WelcomePageV2 : WelcomePageV2Abstract
{
    public WelcomePageV2()
    {
        InitializeComponent();
    }

    private void OnFlipViewSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        FlipViewPager.SelectedPageIndex = UpdateFlipView.SelectedIndex;
    }

    private void OnPipsPagerSelectedIndexChanged(PipsPager sender, PipsPagerSelectedIndexChangedEventArgs args)
    {
        UpdateFlipView.SelectedIndex = sender.SelectedPageIndex;
    }
}
