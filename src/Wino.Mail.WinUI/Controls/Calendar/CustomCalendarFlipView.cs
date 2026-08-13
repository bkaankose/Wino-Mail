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

        RefreshNavigationButtons();

        // Hide navigation buttons
        HideButton(PreviousButtonHorizontal);
        HideButton(NextButtonHorizontal);
        HideButton(PreviousButtonVertical);
        HideButton(NextButtonVertical);

        SelectionChanged -= FlipViewSelectionChanged;
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

    protected virtual void OnSelectedItemChanged(object? oldValue, object? newValue) { }

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        OnContainerPrepared(element, item);
    }

    protected virtual void OnContainerPrepared(DependencyObject element, object item) { }

    protected override DependencyObject GetContainerForItemOverride() => new WinoCalendarFlyoutItem();

    public void GoPreviousFlip()
    {
        InvokeNavigationButton(PreviousButtonHorizontal, PreviousButtonVertical);
    }

    public void GoNextFlip()
    {
        InvokeNavigationButton(NextButtonHorizontal, NextButtonVertical);
    }

    private void RefreshNavigationButtons()
    {
        PreviousButtonHorizontal = GetTemplateChild(PART_PreviousButtonHorizontal) as Button;
        NextButtonHorizontal = GetTemplateChild(PART_NextButtonHorizontal) as Button;
        PreviousButtonVertical = GetTemplateChild(PART_PreviousButtonVertical) as Button;
        NextButtonVertical = GetTemplateChild(PART_NextButtonVertical) as Button;
    }

    private void InvokeNavigationButton(Button? primaryButton, Button? secondaryButton)
    {
        if (Items == null || Items.Count == 0)
            return;

        RefreshNavigationButtons();

        var previousIndex = SelectedIndex;

        if (TryInvokeNavigationButton(primaryButton, previousIndex))
        {
            return;
        }

        TryInvokeNavigationButton(secondaryButton, previousIndex);
    }

    private bool TryInvokeNavigationButton(Button? navigationButton, int previousIndex)
    {
        if (navigationButton == null)
            return false;

        var peer = new ButtonAutomationPeer(navigationButton);
        peer.Invoke();

        return SelectedIndex != previousIndex;
    }
}
