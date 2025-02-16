using System;
using System.Numerics;
using System.Threading.Tasks;
using Windows.UI.Composition;
using Windows.UI.Xaml;

namespace Wino.Extensions;

public static partial class CompositionExtensions
{
    public static void StartSizeAnimation(this UIElement element, Vector2? from = null, Vector2? to = null,
        double duration = 800, int delay = 0, CompositionEasingFunction easing = null, Action completed = null,
        AnimationIterationBehavior iterationBehavior = AnimationIterationBehavior.Count)
    {
        CompositionScopedBatch batch = null;

        var visual = element.Visual();
        var compositor = visual.Compositor;

        if (completed != null)
        {
            batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (s, e) => completed();
        }

        if (to == null)
        {
            to = Vector2.One;
        }

        visual.StartAnimation("Size",
            compositor.CreateVector2KeyFrameAnimation(from, to.Value, duration, delay, easing, iterationBehavior));

        batch?.End();
    }

    public static void StartSizeAnimation(this Visual visual, Vector2? from = null, Vector2? to = null,
        double duration = 800, int delay = 0, CompositionEasingFunction easing = null, Action completed = null,
        AnimationIterationBehavior iterationBehavior = AnimationIterationBehavior.Count)
    {
        CompositionScopedBatch batch = null;
        var compositor = visual.Compositor;

        if (completed != null)
        {
            batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (s, e) => completed();
        }

        if (to == null)
        {
            to = Vector2.One;
        }

        visual.StartAnimation("Size",
            compositor.CreateVector2KeyFrameAnimation(from, to.Value, duration, delay, easing, iterationBehavior));

        batch?.End();
    }

    public static Task StartSizeAnimationAsync(this UIElement element, Vector2? from = null, Vector2? to = null,
        double duration = 800, int delay = 0, CompositionEasingFunction easing = null,
        AnimationIterationBehavior iterationBehavior = AnimationIterationBehavior.Count)
    {
        CompositionScopedBatch batch;

        var visual = element.Visual();
        var compositor = visual.Compositor;

        var taskSource = new TaskCompletionSource<bool>();

        void Completed(object o, CompositionBatchCompletedEventArgs e)
        {
            batch.Completed -= Completed;
            taskSource.SetResult(true);
        }

        batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += Completed;

        if (to == null)
        {
            to = Vector2.One;
        }

        visual.StartAnimation("Size",
            compositor.CreateVector2KeyFrameAnimation(from, to.Value, duration, delay, easing, iterationBehavior));

        batch.End();

        return taskSource.Task;
    }

    public static Task StartSizeAnimationAsync(this Visual visual, Vector2? from = null, Vector2? to = null,
        double duration = 800, int delay = 0, CompositionEasingFunction easing = null,
        AnimationIterationBehavior iterationBehavior = AnimationIterationBehavior.Count)
    {
        CompositionScopedBatch batch;

        var compositor = visual.Compositor;

        var taskSource = new TaskCompletionSource<bool>();

        void Completed(object o, CompositionBatchCompletedEventArgs e)
        {
            batch.Completed -= Completed;
            taskSource.SetResult(true);
        }

        batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += Completed;

        if (to == null)
        {
            to = Vector2.One;
        }

        visual.StartAnimation("Size",
            compositor.CreateVector2KeyFrameAnimation(from, to.Value, duration, delay, easing, iterationBehavior));

        batch.End();

        return taskSource.Task;
    }

}
