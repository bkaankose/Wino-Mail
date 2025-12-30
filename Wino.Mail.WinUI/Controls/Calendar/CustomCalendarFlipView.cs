using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.WinUI.Controls.CalendarFlipView;

namespace Wino.Calendar.Controls;

/// <summary>
/// FlipView that hides the navigation buttons and exposes methods to navigate to the next and previous items with animations.
/// </summary>
public partial class CustomCalendarFlipView : FlipView
{
    private const string PART_PreviousButton = "PreviousButtonHorizontal";
    private const string PART_NextButton = "NextButtonHorizontal";

    private Button? PreviousButton;
    private Button? NextButton;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        PreviousButton = (Button)GetTemplateChild(PART_PreviousButton);
        NextButton = (Button)GetTemplateChild(PART_NextButton);

        // Hide navigation buttons
        PreviousButton.Opacity = NextButton.Opacity = 0;
        PreviousButton.IsHitTestVisible = NextButton.IsHitTestVisible = false;

        this.SelectionChanged += FlipViewSelectionChanged;
    }

    private void FlipViewSelectionChanged(object sender, SelectionChangedEventArgs e) => OnSelectedItemChanged(e.RemovedItems.FirstOrDefault(), e.AddedItems.FirstOrDefault());

    protected virtual void OnSelectedItemChanged(object oldValue, object newValue) { }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        OnContainerPrepared(element, item);
    }

    protected virtual void OnContainerPrepared(DependencyObject element, object item) { }

    protected override DependencyObject GetContainerForItemOverride() => new WinoCalendarFlyoutItem();

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
