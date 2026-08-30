using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Hosting;

namespace Wino.Mail.WinUI.Helpers;

/// <summary>
/// Drives the mail row hover affordance: a rail of actions slides in from the trailing edge over a
/// gradient scrim, while the trailing metadata underneath it fades out of the way.
/// </summary>
/// <remarks>
/// Composition rather than storyboards. These run on rows inside a virtualized list, where the
/// per-frame UI thread cost of a storyboard on every pointer transit is exactly what makes a hover
/// affordance feel cheap; composition animations run off the UI thread.
/// </remarks>
internal static class MailRowHoverActionAnimator
{
    private const string OpacityProperty = "Opacity";
    private const string TranslationProperty = "Translation";
    private const string TranslationXProperty = "Translation.X";

    private static readonly TimeSpan OverlayEnterDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan RailEnterDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ButtonEnterDuration = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MetadataDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(130);
    private static readonly TimeSpan ButtonStagger = TimeSpan.FromMilliseconds(30);

    /// <summary>How far the rail travels in from the trailing edge.</summary>
    private const float RailEnterOffset = 10f;

    /// <summary>How far each button trails the rail as it arrives.</summary>
    private const float ButtonEnterOffset = 6f;

    /// <summary>How far the metadata drifts out as the rail covers it.</summary>
    private const float MetadataExitOffset = 6f;

    /// <summary>
    /// Plays the reveal. <paramref name="buttons"/> must already be filtered to the visible
    /// actions, so a row configured with fewer than three actions staggers without gaps.
    /// </summary>
    public static void Show(
        UIElement overlay,
        IReadOnlyList<UIElement> buttons,
        IReadOnlyList<FrameworkElement> metadata)
    {
        var overlayVisual = GetTranslatableVisual(overlay);
        var compositor = overlayVisual.Compositor;
        var easing = CreateStandardEasing(compositor);

        SetAutomationExposure(overlay, buttons, isExposed: true);
        overlay.IsHitTestVisible = true;
        overlayVisual.StartAnimation(
            OpacityProperty,
            CreateScalarAnimation(compositor, 1f, OverlayEnterDuration, easing));

        overlayVisual.StartAnimation(
            TranslationXProperty,
            CreateScalarAnimation(compositor, 0f, RailEnterDuration, easing));

        for (var index = 0; index < buttons.Count; index++)
        {
            var buttonVisual = GetTranslatableVisual(buttons[index]);
            var delay = TimeSpan.FromTicks(ButtonStagger.Ticks * index);

            buttonVisual.StartAnimation(
                OpacityProperty,
                CreateScalarAnimation(compositor, 1f, ButtonEnterDuration, easing, delay));

            buttonVisual.StartAnimation(
                TranslationXProperty,
                CreateScalarAnimation(compositor, 0f, ButtonEnterDuration, easing, delay));
        }

        foreach (var element in metadata)
        {
            var metadataVisual = GetTranslatableVisual(element);

            metadataVisual.StartAnimation(
                OpacityProperty,
                CreateScalarAnimation(compositor, 0f, MetadataDuration, easing));

            metadataVisual.StartAnimation(
                TranslationXProperty,
                CreateScalarAnimation(compositor, MetadataExitOffset, MetadataDuration, easing));
        }
    }

    /// <summary>
    /// Reverses the reveal. The buttons stop taking input immediately so a pointer that has already
    /// left the row cannot click a button that is still fading out.
    /// </summary>
    public static void Hide(
        UIElement overlay,
        IReadOnlyList<UIElement> buttons,
        IReadOnlyList<FrameworkElement> metadata)
    {
        var overlayVisual = GetTranslatableVisual(overlay);
        var compositor = overlayVisual.Compositor;
        var easing = CreateExitEasing(compositor);

        overlay.IsHitTestVisible = false;

        // The overlay stays realized for the life of the row once it has been hovered, so it has to
        // leave the automation tree here or it keeps reporting its buttons on every row the pointer
        // has ever crossed. Done up front rather than when the fade finishes: waiting on animation
        // completion is not reliable enough to be the only thing keeping the tree clean, and there
        // is nothing worth announcing during a 130ms fade-out.
        SetAutomationExposure(overlay, buttons, isExposed: false);

        overlayVisual.StartAnimation(
            OpacityProperty,
            CreateScalarAnimation(compositor, 0f, ExitDuration, easing));

        // Parks the rail back off the trailing edge so the next reveal slides in rather than
        // fading in place.
        overlayVisual.StartAnimation(
            TranslationXProperty,
            CreateScalarAnimation(compositor, RailEnterOffset, ExitDuration, easing));

        foreach (var button in buttons)
        {
            var buttonVisual = GetTranslatableVisual(button);

            buttonVisual.StartAnimation(
                OpacityProperty,
                CreateScalarAnimation(compositor, 0f, ExitDuration, easing));

            buttonVisual.StartAnimation(
                TranslationXProperty,
                CreateScalarAnimation(compositor, ButtonEnterOffset, ExitDuration, easing));
        }

        foreach (var element in metadata)
        {
            var metadataVisual = GetTranslatableVisual(element);

            metadataVisual.StartAnimation(
                OpacityProperty,
                CreateScalarAnimation(compositor, GetRestOpacity(element), ExitDuration, easing));

            metadataVisual.StartAnimation(
                TranslationXProperty,
                CreateScalarAnimation(compositor, 0f, ExitDuration, easing));
        }
    }

    /// <summary>
    /// Snaps everything back without animating. Containers are recycled while the pointer is still
    /// inside the list, so a row can be handed a different mail without ever raising PointerExited;
    /// without this the new row would render with its metadata still faded out.
    /// </summary>
    public static void Reset(
        UIElement? overlay,
        IReadOnlyList<UIElement> buttons,
        IReadOnlyList<FrameworkElement> metadata)
    {
        if (overlay is not null)
        {
            var overlayVisual = ElementCompositionPreview.GetElementVisual(overlay);

            overlay.IsHitTestVisible = false;
            overlay.Opacity = 0;
            SetAutomationExposure(overlay, buttons, isExposed: false);
            PrepareRailForEnter(overlay);
        }

        foreach (var button in buttons)
        {
            // Goes through GetTranslatableVisual: Translation only exists on the visual once
            // translation is enabled for that element, and stopping an animation on a property
            // that is not there throws.
            PrepareButtonForEnter(button);
        }

        foreach (var element in metadata)
        {
            var metadataVisual = GetTranslatableVisual(element);

            metadataVisual.StopAnimation(OpacityProperty);
            metadataVisual.StopAnimation(TranslationXProperty);
            metadataVisual.Opacity = GetRestOpacity(element);
            metadataVisual.Properties.InsertVector3(TranslationProperty, Vector3.Zero);
        }
    }

    /// <summary>
    /// Puts a freshly realized overlay into its hidden start state. The overlay is deferred until
    /// the first hover, so its buttons are built mid-gesture and would otherwise appear at full
    /// opacity for one frame before the enter animation takes over.
    /// </summary>
    public static void PrepareForEnter(UIElement overlay, IReadOnlyList<UIElement> buttons)
    {
        PrepareRailForEnter(overlay);

        foreach (var button in buttons)
        {
            PrepareButtonForEnter(button);
        }
    }

    private static void PrepareRailForEnter(UIElement overlay)
    {
        var overlayVisual = GetTranslatableVisual(overlay);

        overlayVisual.StopAnimation(OpacityProperty);
        overlayVisual.StopAnimation(TranslationXProperty);
        overlayVisual.Properties.InsertVector3(TranslationProperty, new Vector3(RailEnterOffset, 0f, 0f));
    }

    private static void PrepareButtonForEnter(UIElement button)
    {
        var buttonVisual = GetTranslatableVisual(button);

        buttonVisual.StopAnimation(OpacityProperty);
        buttonVisual.StopAnimation(TranslationXProperty);
        buttonVisual.Opacity = 0f;
        buttonVisual.Properties.InsertVector3(TranslationProperty, new Vector3(ButtonEnterOffset, 0f, 0f));
    }

    /// <summary>
    /// The metadata keeps its authored opacity (the timestamp sits at 0.7, for instance), and the
    /// UIElement.Opacity dependency property is never written during a transit, so it stays the
    /// authority on what "visible" means for that element.
    /// </summary>
    private static float GetRestOpacity(FrameworkElement element) => (float)element.Opacity;

    /// <summary>
    /// Hides the resting rail from assistive technology without collapsing it. Collapsing is the
    /// obvious way to do this, but the overlay has to be laid out before a composition animation on
    /// its subtree will run: un-collapsing and animating in the same tick silently drops the button
    /// animations and they stay at zero opacity.
    /// </summary>
    /// <remarks>
    /// Every button is set individually. Marking only the container Raw leaves its descendants in
    /// the tree, so the buttons keep being reported on every row the pointer has ever crossed.
    /// </remarks>
    private static void SetAutomationExposure(
        UIElement overlay,
        IReadOnlyList<UIElement> buttons,
        bool isExposed)
    {
        var view = isExposed ? AccessibilityView.Content : AccessibilityView.Raw;

        AutomationProperties.SetAccessibilityView(overlay, view);

        foreach (var button in buttons)
        {
            AutomationProperties.SetAccessibilityView(button, view);
        }
    }

    private static Visual GetTranslatableVisual(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        return ElementCompositionPreview.GetElementVisual(element);
    }

    private static CompositionEasingFunction CreateStandardEasing(Compositor compositor)
        => compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1.0f));

    private static CompositionEasingFunction CreateExitEasing(Compositor compositor)
        => compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0.0f), new Vector2(1.0f, 1.0f));

    private static ScalarKeyFrameAnimation CreateScalarAnimation(
        Compositor compositor,
        float finalValue,
        TimeSpan duration,
        CompositionEasingFunction easing,
        TimeSpan? delay = null)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();

        animation.InsertKeyFrame(1f, finalValue, easing);
        animation.Duration = duration;

        if (delay is { } delayTime && delayTime > TimeSpan.Zero)
        {
            animation.DelayTime = delayTime;
            animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }

        return animation;
    }
}
