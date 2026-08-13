using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Wino.Mail.Controls.Playground.Pages;

public sealed partial class IntelligenceProgressPage : Page
{
    public IntelligenceProgressPage()
    {
        InitializeComponent();
    }

    private void AnimationsToggled(object sender, RoutedEventArgs e)
    {
        if (DotsProgress is null)
        {
            return;
        }

        SetAnimationState(AnimationsToggle.IsOn);
    }

    private async void RestartAnimationsClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            SetAnimationState(false);
            await Task.Delay(120);
            SetAnimationState(AnimationsToggle.IsOn);
        }
        catch (Exception)
        {
            SetAnimationState(AnimationsToggle.IsOn);
        }
    }

    private void SetAnimationState(bool isActive)
    {
        DotsProgress.IsActive = isActive;
        CubesProgress.IsActive = isActive;
        TranslateProgress.IsActive = isActive;
        SummarizeProgress.IsActive = isActive;
        RewriteProgress.IsActive = isActive;
    }
}
