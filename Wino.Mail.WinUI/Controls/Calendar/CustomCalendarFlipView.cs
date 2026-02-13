using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Mail.WinUI.Controls.CalendarFlipView;

namespace Wino.Calendar.Controls;

/// <summary>
/// FlipView that hides the navigation buttons and exposes methods to navigate to the next and previous items with animations.
/// </summary>
public partial class CustomCalendarFlipView : FlipView
{
    private const string PART_PreviousButtonHorizontal = "PreviousButtonHorizontal";
    private const string PART_NextButtonHorizontal = "NextButtonHorizontal";
    private const string PART_PreviousButtonVertical = "PreviousButtonVertical";
    private const string PART_NextButtonVertical = "NextButtonVertical";

    public static readonly DependencyProperty DisplayTypeProperty = DependencyProperty.Register(
        nameof(DisplayType),
        typeof(CalendarDisplayType),
        typeof(CustomCalendarFlipView),
        new PropertyMetadata(CalendarDisplayType.Week));

    public CalendarDisplayType DisplayType
    {
        get => (CalendarDisplayType)GetValue(DisplayTypeProperty);
        set => SetValue(DisplayTypeProperty, value);
    }

    private Button? PreviousButtonHorizontal;
    private Button? NextButtonHorizontal;
    private Button? PreviousButtonVertical;
    private Button? NextButtonVertical;

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        PreviousButtonHorizontal = GetTemplateChild(PART_PreviousButtonHorizontal) as Button;
        NextButtonHorizontal = GetTemplateChild(PART_NextButtonHorizontal) as Button;
        PreviousButtonVertical = GetTemplateChild(PART_PreviousButtonVertical) as Button;
        NextButtonVertical = GetTemplateChild(PART_NextButtonVertical) as Button;

        // Hide navigation buttons
        HideButton(PreviousButtonHorizontal);
        HideButton(NextButtonHorizontal);
        HideButton(PreviousButtonVertical);
        HideButton(NextButtonVertical);

        SelectionChanged += FlipViewSelectionChanged;
    }

    private static void HideButton(Button? button)
    {
        if (button == null) return;

        button.Opacity = 0;
        button.IsHitTestVisible = false;
    }

    private void FlipViewSelectionChanged(object sender, SelectionChangedEventArgs e)
        => OnSelectedItemChanged(e.RemovedItems.FirstOrDefault(), e.AddedItems.FirstOrDefault());

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
        var previousButton = DisplayType == CalendarDisplayType.Month
            ? PreviousButtonVertical ?? PreviousButtonHorizontal
            : PreviousButtonHorizontal ?? PreviousButtonVertical;

        if (previousButton == null) return;

        var backPeer = new ButtonAutomationPeer(previousButton);
        backPeer.Invoke();
    }

    public void GoNextFlip()
    {
        var nextButton = DisplayType == CalendarDisplayType.Month
            ? NextButtonVertical ?? NextButtonHorizontal
            : NextButtonHorizontal ?? NextButtonVertical;

        if (nextButton == null) return;

        var nextPeer = new ButtonAutomationPeer(nextButton);
        nextPeer.Invoke();
    }
}
