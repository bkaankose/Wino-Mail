using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Wino.Calendar.Controls;

public sealed partial class CalendarMoreEventsButton : UserControl
{
    [GeneratedDependencyProperty]
    public partial CalendarDayOverflow? Overflow { get; set; }

    public CalendarMoreEventsButton()
    {
        InitializeComponent();
    }

    public void HideFlyout() => HiddenItemsFlyout.Hide();

    private void MoreButtonClick(object sender, RoutedEventArgs e)
    {
        if (Overflow is null || Overflow.HiddenItems.Count == 0)
        {
            return;
        }

        FlyoutBase.ShowAttachedFlyout(MoreButton);
    }

    private void HiddenItemsTapped(object sender, TappedRoutedEventArgs e) => HiddenItemsFlyout.Hide();
}
