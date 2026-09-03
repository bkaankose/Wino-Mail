using System;
using System.Numerics;
using System.Windows.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.UI.ViewManagement;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.HoverActions;
using Wino.Mail.Controls.HoverActions;

namespace Wino.Mail.Controls.MailListView;

/// <summary>
/// A mail row container that owns hover action visibility itself instead of delegating it to
/// <c>CommonStates</c>. The visual state manager hides the actions on every pointer exit, which
/// is wrong while a context menu opened from the row is still on screen, so the show/hide policy
/// and its entrance animation live here.
/// </summary>
[TemplatePart(Name = HoverActionsHostPartName, Type = typeof(FrameworkElement))]
[TemplatePart(Name = HoverActionsPresenterPartName, Type = typeof(FrameworkElement))]
public sealed partial class WinoMailListViewItem : ListViewItem
{
    private const string HoverActionsHostPartName = "HoverActionsHost";
    private const string HoverActionsPresenterPartName = "HoverActionsPresenter";

    private const float SlideDistance = 24f;
    private const float PopupStartScale = 0.85f;

    private static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(90);
    private static readonly UISettings SharedUISettings = new();

    private FrameworkElement? _hoverActionsHost;
    private FrameworkElement? _hoverActionsPresenter;
    private Visual? _hoverActionsVisual;
    private long _hoverActionsGeneration;
    private bool _isPointerOver;
    private bool _isContextMenuOpen;
    private bool _areHoverActionsShown;

    internal WinoMailListView? OwnerList { get; set; }

    [GeneratedDependencyProperty]
    public partial MailListRow? Row { get; set; }

    [GeneratedDependencyProperty(DefaultValue = HoverActionKind.None)]
    public partial HoverActionKind LeftHoverAction { get; set; }

    [GeneratedDependencyProperty(DefaultValue = HoverActionKind.None)]
    public partial HoverActionKind CenterHoverAction { get; set; }

    [GeneratedDependencyProperty(DefaultValue = HoverActionKind.None)]
    public partial HoverActionKind RightHoverAction { get; set; }

    [GeneratedDependencyProperty]
    public partial object? HoverActionLabels { get; set; }

    [GeneratedDependencyProperty]
    public partial ICommand? HoverActionCommand { get; set; }

    [GeneratedDependencyProperty(DefaultValue = HoverActionAnimation.Popup)]
    public partial HoverActionAnimation HoverActionAnimation { get; set; }

    [GeneratedDependencyProperty(DefaultValue = HoverActionPosition.RightCenter)]
    public partial HoverActionPosition HoverActionPosition { get; set; }

    [GeneratedDependencyProperty(DefaultValue = HoverActionButtonSize.Small)]
    public partial HoverActionButtonSize HoverActionButtonSize { get; set; }

    /// <summary>
    /// Preview hosts show the swipe affordance but must not run the operation behind it.
    /// </summary>
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool AreSwipeOperationsEnabled { get; set; }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        DetachHoverActionParts();

        _hoverActionsHost = GetTemplateChild(HoverActionsHostPartName) as FrameworkElement;

        // Templates that do not carry a dedicated presenter animate the host itself.
        _hoverActionsPresenter = GetTemplateChild(HoverActionsPresenterPartName) as FrameworkElement
            ?? _hoverActionsHost;

        if (_hoverActionsPresenter is not null)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(_hoverActionsPresenter, true);
            _hoverActionsVisual = ElementCompositionPreview.GetElementVisual(_hoverActionsPresenter);
            _hoverActionsPresenter.SizeChanged += OnHoverActionsPresenterSizeChanged;
            UpdateHoverActionsCenterPoint();
        }

        ResetHoverActions();
    }

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        UpdatePointerOver(e, isOver: true);
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs e)
    {
        base.OnPointerMoved(e);

        // Re-syncs hover after a context menu dismissal, which leaves the row without a fresh
        // enter even though the pointer never left it.
        UpdatePointerOver(e, isOver: true);
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        UpdatePointerOver(e, isOver: false);
    }

    protected override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        base.OnPointerCanceled(e);
        SetPointerOver(false);
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        SetPointerOver(false);
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        OwnerList?.RecordPointerPressed(Row, IsSelected);
        base.OnPointerPressed(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new WinoMailListViewItemAutomationPeer(this);

    /// <summary>
    /// Keeps hover actions on screen while a context menu opened from this row is showing.
    /// </summary>
    internal void SetContextMenuOpen(bool isOpen)
    {
        if (_isContextMenuOpen == isOpen)
        {
            return;
        }

        _isContextMenuOpen = isOpen;

        // The menu swallowed the pointer exit, so hover is dropped on close and restored by the
        // next pointer move over the row.
        if (!isOpen)
        {
            _isPointerOver = false;
        }

        UpdateHoverActionsVisibility(useTransitions: true);
    }

    /// <summary>
    /// Snaps hover actions back to hidden. Containers are recycled, so a reused row must not
    /// inherit the previous row's hover state or animate out of it.
    /// </summary>
    internal void ResetHoverActions()
    {
        _isPointerOver = false;
        _isContextMenuOpen = false;
        _areHoverActionsShown = false;
        _hoverActionsGeneration++;

        if (_hoverActionsVisual is not null)
        {
            _hoverActionsVisual.StopAnimation(nameof(Visual.Opacity));
            _hoverActionsVisual.StopAnimation(nameof(Visual.Scale));
            _hoverActionsVisual.StopAnimation("Translation");
            ApplyRestingState(isShown: false);
        }

        if (_hoverActionsHost is not null)
        {
            _hoverActionsHost.IsHitTestVisible = false;
            _hoverActionsHost.Visibility = Visibility.Collapsed;
        }
    }

    private void DetachHoverActionParts()
    {
        if (_hoverActionsPresenter is not null)
        {
            _hoverActionsPresenter.SizeChanged -= OnHoverActionsPresenterSizeChanged;
        }

        _hoverActionsHost = null;
        _hoverActionsPresenter = null;
        _hoverActionsVisual = null;
    }

    private void OnHoverActionsPresenterSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateHoverActionsCenterPoint();

    private void UpdateHoverActionsCenterPoint()
    {
        if (_hoverActionsPresenter is null || _hoverActionsVisual is null)
        {
            return;
        }

        _hoverActionsVisual.CenterPoint = new Vector3(
            (float)_hoverActionsPresenter.ActualWidth / 2f,
            (float)_hoverActionsPresenter.ActualHeight / 2f,
            0f);
    }

    private void UpdatePointerOver(PointerRoutedEventArgs e, bool isOver)
    {
        // Touch contacts raise enter and exit pairs while the list is flicked. Hover actions are
        // a pointer affordance and must not flash during a scroll.
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch)
        {
            return;
        }

        SetPointerOver(isOver);
    }

    private void SetPointerOver(bool isOver)
    {
        // An open context menu owns the pointer, so the exit it causes is not a real hover loss.
        if (!isOver && _isContextMenuOpen)
        {
            return;
        }

        if (_isPointerOver == isOver)
        {
            return;
        }

        _isPointerOver = isOver;
        UpdateHoverActionsVisibility(useTransitions: true);
    }

    private void UpdateHoverActionsVisibility(bool useTransitions)
    {
        var shouldShow = _isPointerOver || _isContextMenuOpen;

        if (_areHoverActionsShown == shouldShow)
        {
            return;
        }

        _areHoverActionsShown = shouldShow;

        if (_hoverActionsHost is null || _hoverActionsVisual is null)
        {
            return;
        }

        var generation = ++_hoverActionsGeneration;
        var animation = useTransitions && SharedUISettings.AnimationsEnabled
            ? HoverActionAnimation
            : HoverActionAnimation.NoAnimation;

        _hoverActionsHost.IsHitTestVisible = shouldShow;

        if (shouldShow)
        {
            _hoverActionsHost.Visibility = Visibility.Visible;
        }

        if (animation == HoverActionAnimation.NoAnimation)
        {
            ApplyRestingState(shouldShow);

            if (!shouldShow)
            {
                _hoverActionsHost.Visibility = Visibility.Collapsed;
            }

            return;
        }

        if (shouldShow)
        {
            PlayShowAnimation(animation);
        }
        else
        {
            PlayHideAnimation(animation, generation);
        }
    }

    private void ApplyRestingState(bool isShown)
    {
        if (_hoverActionsVisual is null)
        {
            return;
        }

        _hoverActionsVisual.Opacity = isShown ? 1f : 0f;
        _hoverActionsVisual.Scale = Vector3.One;
        _hoverActionsVisual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    private void PlayShowAnimation(HoverActionAnimation animation)
    {
        if (_hoverActionsVisual is null)
        {
            return;
        }

        var compositor = _hoverActionsVisual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.1f, 0.9f),
            new Vector2(0.2f, 1f));

        UpdateHoverActionsCenterPoint();

        _hoverActionsVisual.Opacity = 0f;
        _hoverActionsVisual.Scale = animation == HoverActionAnimation.Popup
            ? new Vector3(PopupStartScale, PopupStartScale, 1f)
            : Vector3.One;
        _hoverActionsVisual.Properties.InsertVector3(
            "Translation",
            animation == HoverActionAnimation.Slide ? GetSlideOffset() : Vector3.Zero);

        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Duration = ShowDuration;
        opacityAnimation.InsertKeyFrame(1f, 1f, easing);
        _hoverActionsVisual.StartAnimation(nameof(Visual.Opacity), opacityAnimation);

        if (animation == HoverActionAnimation.Popup)
        {
            var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
            scaleAnimation.Duration = ShowDuration;
            scaleAnimation.InsertKeyFrame(1f, Vector3.One, easing);
            _hoverActionsVisual.StartAnimation(nameof(Visual.Scale), scaleAnimation);
        }
        else
        {
            var translationAnimation = compositor.CreateVector3KeyFrameAnimation();
            translationAnimation.Duration = ShowDuration;
            translationAnimation.InsertKeyFrame(1f, Vector3.Zero, easing);
            _hoverActionsVisual.StartAnimation("Translation", translationAnimation);
        }
    }

    private void PlayHideAnimation(HoverActionAnimation animation, long generation)
    {
        if (_hoverActionsVisual is null)
        {
            return;
        }

        var compositor = _hoverActionsVisual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.7f, 0f),
            new Vector2(1f, 0.5f));

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

        var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
        opacityAnimation.Duration = HideDuration;
        opacityAnimation.InsertKeyFrame(1f, 0f, easing);
        _hoverActionsVisual.StartAnimation(nameof(Visual.Opacity), opacityAnimation);

        if (animation == HoverActionAnimation.Popup)
        {
            var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
            scaleAnimation.Duration = HideDuration;
            scaleAnimation.InsertKeyFrame(1f, new Vector3(PopupStartScale, PopupStartScale, 1f), easing);
            _hoverActionsVisual.StartAnimation(nameof(Visual.Scale), scaleAnimation);
        }
        else
        {
            var translationAnimation = compositor.CreateVector3KeyFrameAnimation();
            translationAnimation.Duration = HideDuration;
            translationAnimation.InsertKeyFrame(1f, GetSlideOffset(), easing);
            _hoverActionsVisual.StartAnimation("Translation", translationAnimation);
        }

        batch.End();

        batch.Completed += (_, _) =>
        {
            // A newer show or a container recycle already claimed the visual.
            if (generation != _hoverActionsGeneration || _hoverActionsHost is null)
            {
                return;
            }

            _hoverActionsHost.Visibility = Visibility.Collapsed;
        };
    }

    private Vector3 GetSlideOffset() => HoverActionPosition switch
    {
        HoverActionPosition.TopCenter => new Vector3(0f, -SlideDistance, 0f),
        HoverActionPosition.BottomCenter => new Vector3(0f, SlideDistance, 0f),
        _ => new Vector3(SlideDistance, 0f, 0f),
    };
}
