using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Wino.Mail.WinUI.Helpers;

internal sealed class FeatheredHoverActionAnimator : IDisposable
{
    private static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(190);
    private static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(105);
    private static readonly Vector3 HiddenActionOffset = new(12f, 0f, 0f);

    private readonly FrameworkElement _overlay;
    private readonly Visual _veilVisual;
    private readonly Visual _actionVisual;
    private readonly Compositor _compositor;
    private readonly CubicBezierEasingFunction _showEasing;
    private readonly CubicBezierEasingFunction _hideEasing;

    private ScalarKeyFrameAnimation? _veilOpacityAnimation;
    private ScalarKeyFrameAnimation? _actionOpacityAnimation;
    private Vector3KeyFrameAnimation? _actionOffsetAnimation;
    private CompositionScopedBatch? _hideBatch;
    private Windows.Foundation.TypedEventHandler<object, CompositionBatchCompletedEventArgs>? _hideCompletedHandler;
    private Action? _hideCompletion;
    private bool _isDisposed;

    public FeatheredHoverActionAnimator(
        FrameworkElement overlay,
        FrameworkElement veil,
        FrameworkElement actionHost)
    {
        _overlay = overlay;
        _veilVisual = ElementCompositionPreview.GetElementVisual(veil);
        _actionVisual = ElementCompositionPreview.GetElementVisual(actionHost);
        _compositor = _veilVisual.Compositor;
        _showEasing = _compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f),
            new Vector2(0.3f, 1f));
        _hideEasing = _compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.4f, 0f),
            new Vector2(1f, 1f));

        ResetVisuals();
    }

    public void Show()
    {
        if (_isDisposed)
            return;

        CancelPendingHide();

        if (_overlay.Visibility != Visibility.Visible)
        {
            ResetVisuals();
            _overlay.Visibility = Visibility.Visible;
        }

        StartAnimations(1f, Vector3.Zero, ShowDuration, _showEasing);
    }

    public void Hide(Action? completed = null)
    {
        if (_isDisposed || _overlay.Visibility != Visibility.Visible)
        {
            completed?.Invoke();
            return;
        }

        CancelPendingHide();
        _hideCompletion = completed;

        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _hideBatch = batch;
        _hideCompletedHandler = HideBatchCompleted;
        batch.Completed += _hideCompletedHandler;

        StartAnimations(0f, HiddenActionOffset, HideDuration, _hideEasing);
        batch.End();
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        CancelPendingHide();
        StopAndDisposeAnimations();
        _showEasing.Dispose();
        _hideEasing.Dispose();
        _overlay.Visibility = Visibility.Collapsed;
    }

    private void StartAnimations(
        float opacity,
        Vector3 actionOffset,
        TimeSpan duration,
        CompositionEasingFunction easing)
    {
        var veilOpacityAnimation = CreateOpacityAnimation(opacity, duration, easing);
        var actionOpacityAnimation = CreateOpacityAnimation(opacity, duration, easing);
        var actionOffsetAnimation = _compositor.CreateVector3KeyFrameAnimation();
        actionOffsetAnimation.Duration = duration;
        actionOffsetAnimation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        actionOffsetAnimation.InsertKeyFrame(1f, actionOffset, easing);

        _veilVisual.StartAnimation(nameof(Visual.Opacity), veilOpacityAnimation);
        _actionVisual.StartAnimation(nameof(Visual.Opacity), actionOpacityAnimation);
        _actionVisual.StartAnimation(nameof(Visual.Offset), actionOffsetAnimation);

        _veilOpacityAnimation?.Dispose();
        _actionOpacityAnimation?.Dispose();
        _actionOffsetAnimation?.Dispose();

        _veilOpacityAnimation = veilOpacityAnimation;
        _actionOpacityAnimation = actionOpacityAnimation;
        _actionOffsetAnimation = actionOffsetAnimation;
    }

    private ScalarKeyFrameAnimation CreateOpacityAnimation(
        float opacity,
        TimeSpan duration,
        CompositionEasingFunction easing)
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = duration;
        animation.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        animation.InsertKeyFrame(1f, opacity, easing);
        return animation;
    }

    private void HideBatchCompleted(object? sender, CompositionBatchCompletedEventArgs e)
    {
        var completed = _hideCompletion;
        ReleaseHideBatch();
        StopAndDisposeAnimations();
        _overlay.Visibility = Visibility.Collapsed;
        ResetVisuals();
        completed?.Invoke();
    }

    private void CancelPendingHide()
    {
        _hideCompletion = null;
        ReleaseHideBatch();
    }

    private void ReleaseHideBatch()
    {
        if (_hideBatch != null && _hideCompletedHandler != null)
        {
            _hideBatch.Completed -= _hideCompletedHandler;
        }

        _hideBatch?.Dispose();
        _hideBatch = null;
        _hideCompletedHandler = null;
    }

    private void StopAndDisposeAnimations()
    {
        _veilVisual.StopAnimation(nameof(Visual.Opacity));
        _actionVisual.StopAnimation(nameof(Visual.Opacity));
        _actionVisual.StopAnimation(nameof(Visual.Offset));

        _veilOpacityAnimation?.Dispose();
        _actionOpacityAnimation?.Dispose();
        _actionOffsetAnimation?.Dispose();
        _veilOpacityAnimation = null;
        _actionOpacityAnimation = null;
        _actionOffsetAnimation = null;
    }

    private void ResetVisuals()
    {
        _veilVisual.Opacity = 0f;
        _actionVisual.Opacity = 0f;
        _actionVisual.Offset = HiddenActionOffset;
    }
}
