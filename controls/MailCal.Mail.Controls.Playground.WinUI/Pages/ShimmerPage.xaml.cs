using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MailCal.Mail.Controls.Shimmer;

namespace MailCal.Mail.Controls.Playground.Pages;

public sealed partial class ShimmerPage : Page
{
    public ShimmerPage()
    {
        InitializeComponent();
    }

    private void AnimationsToggled(object sender, RoutedEventArgs e)
    {
        if (SampleSurface is null)
        {
            return;
        }

        SetSweepState(AnimationsToggle.IsOn);
    }

    private void ThemeToggled(object sender, RoutedEventArgs e)
    {
        if (SampleSurface is null)
        {
            return;
        }

        // Only the sample surface flips, so both palettes can be compared without leaving the page.
        SampleSurface.RequestedTheme = ThemeToggle.IsOn ? ElementTheme.Dark : ElementTheme.Light;
    }

    private void SetSweepState(bool isActive)
    {
        foreach (var shimmer in EnumerateShimmers(SampleSurface))
        {
            shimmer.IsActive = isActive;
        }
    }

    private static IEnumerable<MailCalShimmer> EnumerateShimmers(DependencyObject root)
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            if (child is MailCalShimmer shimmer)
            {
                yield return shimmer;
                continue;
            }

            foreach (var nested in EnumerateShimmers(child))
            {
                yield return nested;
            }
        }
    }
}
