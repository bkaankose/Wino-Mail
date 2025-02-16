using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Animation;

namespace Wino.Extensions;

public static class AnimationExtensions
{
    #region Composition

    public static ScalarKeyFrameAnimation CreateScalarKeyFrameAnimation(this Compositor compositor, float? from, float to,
        double duration, double delay, CompositionEasingFunction easing, AnimationIterationBehavior iterationBehavior)
    {
        var animation = compositor.CreateScalarKeyFrameAnimation();

        animation.Duration = TimeSpan.FromMilliseconds(duration);
        if (!delay.Equals(0)) animation.DelayTime = TimeSpan.FromMilliseconds(delay);
        if (from.HasValue) animation.InsertKeyFrame(0.0f, from.Value, easing);
        animation.InsertKeyFrame(1.0f, to, easing);
        animation.IterationBehavior = iterationBehavior;

        return animation;
    }

    public static Vector2KeyFrameAnimation CreateVector2KeyFrameAnimation(this Compositor compositor, Vector2? from, Vector2 to,
        double duration, double delay, CompositionEasingFunction easing, AnimationIterationBehavior iterationBehavior)
    {
        var animation = compositor.CreateVector2KeyFrameAnimation();

        animation.Duration = TimeSpan.FromMilliseconds(duration);
        animation.DelayTime = TimeSpan.FromMilliseconds(delay);
        if (from.HasValue) animation.InsertKeyFrame(0.0f, from.Value, easing);
        animation.InsertKeyFrame(1.0f, to, easing);
        animation.IterationBehavior = iterationBehavior;

        return animation;
    }

    public static Vector3KeyFrameAnimation CreateVector3KeyFrameAnimation(this Compositor compositor, Vector2? from, Vector2 to,
        double duration, double delay, CompositionEasingFunction easing, AnimationIterationBehavior iterationBehavior)
    {
        var animation = compositor.CreateVector3KeyFrameAnimation();

        animation.Duration = TimeSpan.FromMilliseconds(duration);
        animation.DelayTime = TimeSpan.FromMilliseconds(delay);
        if (from.HasValue) animation.InsertKeyFrame(0.0f, new Vector3(from.Value, 1.0f), easing);
        animation.InsertKeyFrame(1.0f, new Vector3(to, 1.0f), easing);
        animation.IterationBehavior = iterationBehavior;

        return animation;
    }

    public static Vector3KeyFrameAnimation CreateVector3KeyFrameAnimation(this Compositor compositor, Vector3? from, Vector3 to,
        double duration, double delay, CompositionEasingFunction easing, AnimationIterationBehavior iterationBehavior)
    {
        var animation = compositor.CreateVector3KeyFrameAnimation();

        animation.Duration = TimeSpan.FromMilliseconds(duration);
        animation.DelayTime = TimeSpan.FromMilliseconds(delay);
        if (from.HasValue) animation.InsertKeyFrame(0.0f, from.Value, easing);
        animation.InsertKeyFrame(1.0f, to, easing);
        animation.IterationBehavior = iterationBehavior;

        return animation;
    }

    #endregion

    #region Xaml Storyboard

    public static void Animate(this DependencyObject target, double? from, double to,
      string propertyPath, int duration = 400, int startTime = 0,
      EasingFunctionBase easing = null, Action completed = null, bool enableDependentAnimation = false)
    {
        if (easing == null)
        {
            easing = new ExponentialEase();
        }

        var db = new DoubleAnimation
        {
            EnableDependentAnimation = enableDependentAnimation,
            To = to,
            From = from,
            EasingFunction = easing,
            Duration = TimeSpan.FromMilliseconds(duration)
        };
        Storyboard.SetTarget(db, target);
        Storyboard.SetTargetProperty(db, propertyPath);

        var sb = new Storyboard
        {
            BeginTime = TimeSpan.FromMilliseconds(startTime)
        };

        if (completed != null)
        {
            sb.Completed += (s, e) =>
            {
                completed();
            };
        }

        sb.Children.Add(db);
        sb.Begin();
    }

    #endregion
}
