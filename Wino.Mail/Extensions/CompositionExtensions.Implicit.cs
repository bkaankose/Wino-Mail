using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;

namespace Wino.Extensions
{
    public static partial class CompositionExtensions
    {
        public static void EnableFluidVisibilityAnimation(this UIElement element, AnimationAxis? axis = null,
            float showFromOffset = 0.0f, float hideToOffset = 0.0f, Vector3? centerPoint = null,
            float showFromScale = 1.0f, float hideToScale = 1.0f, float showDuration = 800.0f, float hideDuration = 800.0f,
            int showDelay = 0, int hideDelay = 0, bool animateOpacity = true)
        {
            var elementVisual = element.Visual();
            var compositor = elementVisual.Compositor;
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);

            ScalarKeyFrameAnimation hideOpacityAnimation = null;
            ScalarKeyFrameAnimation showOpacityAnimation = null;
            ScalarKeyFrameAnimation hideOffsetAnimation = null;
            ScalarKeyFrameAnimation showOffsetAnimation = null;
            Vector2KeyFrameAnimation hideScaleAnimation = null;
            Vector2KeyFrameAnimation showeScaleAnimation = null;

            if (animateOpacity)
            {
                hideOpacityAnimation = compositor.CreateScalarKeyFrameAnimation();
                hideOpacityAnimation.InsertKeyFrame(1.0f, 0.0f);
                hideOpacityAnimation.Duration = TimeSpan.FromMilliseconds(hideDuration);
                hideOpacityAnimation.DelayTime = TimeSpan.FromMilliseconds(hideDelay);
                hideOpacityAnimation.Target = "Opacity";
            }

            if (!hideToOffset.Equals(0.0f))
            {
                hideOffsetAnimation = compositor.CreateScalarKeyFrameAnimation();
                hideOffsetAnimation.InsertKeyFrame(1.0f, hideToOffset);
                hideOffsetAnimation.Duration = TimeSpan.FromMilliseconds(hideDuration);
                hideOffsetAnimation.DelayTime = TimeSpan.FromMilliseconds(hideDelay);
                hideOffsetAnimation.Target = $"Translation.{axis}";
            }

            if (centerPoint.HasValue)
            {
                elementVisual.CenterPoint = centerPoint.Value;
            }

            if (!hideToScale.Equals(1.0f))
            {
                hideScaleAnimation = compositor.CreateVector2KeyFrameAnimation();
                hideScaleAnimation.InsertKeyFrame(1.0f, new Vector2(hideToScale));
                hideScaleAnimation.Duration = TimeSpan.FromMilliseconds(hideDuration);
                hideScaleAnimation.DelayTime = TimeSpan.FromMilliseconds(hideDelay);
                hideScaleAnimation.Target = "Scale.XY";
            }

            var hideAnimationGroup = compositor.CreateAnimationGroup();
            if (hideOpacityAnimation != null)
            {
                hideAnimationGroup.Add(hideOpacityAnimation);
            }
            if (hideOffsetAnimation != null)
            {
                hideAnimationGroup.Add(hideOffsetAnimation);
            }
            if (hideScaleAnimation != null)
            {
                hideAnimationGroup.Add(hideScaleAnimation);
            }

            ElementCompositionPreview.SetImplicitHideAnimation(element, hideAnimationGroup);

            if (animateOpacity)
            {
                showOpacityAnimation = compositor.CreateScalarKeyFrameAnimation();
                showOpacityAnimation.InsertKeyFrame(1.0f, 1.0f);
                showOpacityAnimation.Duration = TimeSpan.FromMilliseconds(showDuration);
                showOpacityAnimation.DelayTime = TimeSpan.FromMilliseconds(showDelay);
                showOpacityAnimation.Target = "Opacity";
            }

            if (!showFromOffset.Equals(0.0f))
            {
                showOffsetAnimation = compositor.CreateScalarKeyFrameAnimation();
                showOffsetAnimation.InsertKeyFrame(0.0f, showFromOffset);
                showOffsetAnimation.InsertKeyFrame(1.0f, 0.0f);
                showOffsetAnimation.Duration = TimeSpan.FromMilliseconds(showDuration);
                showOffsetAnimation.DelayTime = TimeSpan.FromMilliseconds(showDelay);
                showOffsetAnimation.Target = $"Translation.{axis}";
            }

            if (!showFromScale.Equals(1.0f))
            {
                showeScaleAnimation = compositor.CreateVector2KeyFrameAnimation();
                showeScaleAnimation.InsertKeyFrame(0.0f, new Vector2(showFromScale));
                showeScaleAnimation.InsertKeyFrame(1.0f, Vector2.One);
                showeScaleAnimation.Duration = TimeSpan.FromMilliseconds(showDuration);
                showeScaleAnimation.DelayTime = TimeSpan.FromMilliseconds(showDelay);
                showeScaleAnimation.Target = "Scale.XY";
            }

            var showAnimationGroup = compositor.CreateAnimationGroup();
            if (showOpacityAnimation != null)
            {
                showAnimationGroup.Add(showOpacityAnimation);
            }
            if (showOffsetAnimation != null)
            {
                showAnimationGroup.Add(showOffsetAnimation);
            }
            if (showeScaleAnimation != null)
            {
                showAnimationGroup.Add(showeScaleAnimation);
            }

            ElementCompositionPreview.SetImplicitShowAnimation(element, showAnimationGroup);
        }

        public static void EnableImplicitAnimation(this UIElement element, VisualPropertyType typeToAnimate,
            double duration = 800, double delay = 0, CompositionEasingFunction easing = null)
        {
            var visual = element.Visual();
            var compositor = visual.Compositor;

            var animationCollection = compositor.CreateImplicitAnimationCollection();

            foreach (var type in UtilExtensions.GetValues<VisualPropertyType>())
            {
                if (!typeToAnimate.HasFlag(type)) continue;

                var animation = CreateAnimationByType(compositor, type, duration, delay, easing);

                if (animation != null)
                {
                    animationCollection[type.ToString()] = animation;
                }
            }

            visual.ImplicitAnimations = animationCollection;
        }

        public static void EnableImplicitAnimation(this Visual visual, VisualPropertyType typeToAnimate,
            double duration = 800, double delay = 0, CompositionEasingFunction easing = null)
        {
            var compositor = visual.Compositor;

            var animationCollection = compositor.CreateImplicitAnimationCollection();

            foreach (var type in UtilExtensions.GetValues<VisualPropertyType>())
            {
                if (!typeToAnimate.HasFlag(type)) continue;

                var animation = CreateAnimationByType(compositor, type, duration, delay, easing);

                if (animation != null)
                {
                    animationCollection[type.ToString()] = animation;
                }
            }

            visual.ImplicitAnimations = animationCollection;
        }

        private static KeyFrameAnimation CreateAnimationByType(Compositor compositor, VisualPropertyType type,
            double duration = 800, double delay = 0, CompositionEasingFunction easing = null)
        {
            KeyFrameAnimation animation;

            switch (type)
            {
                case VisualPropertyType.Offset:
                case VisualPropertyType.Scale:
                    animation = compositor.CreateVector3KeyFrameAnimation();
                    break;
                case VisualPropertyType.Size:
                    animation = compositor.CreateVector2KeyFrameAnimation();
                    break;
                case VisualPropertyType.Opacity:
                case VisualPropertyType.RotationAngleInDegrees:
                    animation = compositor.CreateScalarKeyFrameAnimation();
                    break;
                default:
                    return null;
            }

            animation.InsertExpressionKeyFrame(1.0f, "this.FinalValue", easing);
            animation.Duration = TimeSpan.FromMilliseconds(duration);
            animation.DelayTime = TimeSpan.FromMilliseconds(delay);
            animation.Target = type.ToString();

            return animation;
        }
    }
}
