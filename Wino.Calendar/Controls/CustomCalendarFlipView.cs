using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;

namespace Wino.Calendar.Controls;

/// <summary>
/// FlipView that hides the navigation buttons and exposes methods to navigate to the next and previous items with animations.
/// </summary>
public partial class CustomCalendarFlipView : FlipView
{
    private const string PART_PreviousButton = "PreviousButtonHorizontal";
    private const string PART_NextButton = "NextButtonHorizontal";

    private Button PreviousButton;
    private Button NextButton;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        PreviousButton = GetTemplateChild(PART_PreviousButton) as Button;
        NextButton = GetTemplateChild(PART_NextButton) as Button;

        // Hide navigation buttons
        PreviousButton.Opacity = NextButton.Opacity = 0;
        PreviousButton.IsHitTestVisible = NextButton.IsHitTestVisible = false;

        var t = FindName("ScrollingHost");
    }

    public void GoPreviousFlip()
    {
        var backPeer = new ButtonAutomationPeer(PreviousButton);
        backPeer.Invoke();
    }

    public void GoNextFlip()
    {
        var nextPeer = new ButtonAutomationPeer(NextButton);
        nextPeer.Invoke();
    }
}
