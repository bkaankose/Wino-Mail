using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using System.Numerics;
using Windows.UI;

namespace Wino.Mail.Controls.IntelligenceProgressRing;

public sealed partial class OrbitDotsAnimatedVisualSource : WinoAnimatedVisualSource
{
    public OrbitDotsAnimatedVisualSource() : base(Color.FromArgb(0, 0, 0, 0), TimeSpan.FromSeconds(1.2)) { }

    protected override WinoCompositionAnimatedVisual CreateVisual(Compositor compositor) =>
        new OrbitDotsAnimatedVisual(compositor, Foreground, Duration);
}

public sealed partial class CubesAnimatedVisualSource : WinoAnimatedVisualSource
{
    public CubesAnimatedVisualSource(Color foreground) : base(foreground, TimeSpan.FromSeconds(2.1)) { }

    protected override WinoCompositionAnimatedVisual CreateVisual(Compositor compositor) =>
        new CubesAnimatedVisual(compositor, Foreground, Duration);
}

public sealed partial class TranslateAnimatedVisualSource : WinoAnimatedVisualSource
{
    public TranslateAnimatedVisualSource(Color foreground) : base(foreground, TimeSpan.FromSeconds(2)) { }

    protected override WinoCompositionAnimatedVisual CreateVisual(Compositor compositor) =>
        new TranslateAnimatedVisual(compositor, Foreground, Duration);
}

public sealed partial class SummarizeAnimatedVisualSource : WinoAnimatedVisualSource
{
    public SummarizeAnimatedVisualSource(Color foreground) : base(foreground, TimeSpan.FromSeconds(1.8)) { }

    protected override WinoCompositionAnimatedVisual CreateVisual(Compositor compositor) =>
        new SummarizeAnimatedVisual(compositor, Foreground, Duration);
}

public sealed partial class RewriteAnimatedVisualSource : WinoAnimatedVisualSource
{
    public RewriteAnimatedVisualSource(Color foreground) : base(foreground, TimeSpan.FromSeconds(2.2)) { }

    protected override WinoCompositionAnimatedVisual CreateVisual(Compositor compositor) =>
        new RewriteAnimatedVisual(compositor, Foreground, Duration);
}

public abstract partial class WinoAnimatedVisualSource : IAnimatedVisualSource, IAnimatedVisualSource2
{
    private static readonly IReadOnlyDictionary<string, double> EmptyMarkers =
        new Dictionary<string, double>();

    protected WinoAnimatedVisualSource(Color foreground, TimeSpan duration)
    {
        Foreground = foreground;
        Duration = duration;
    }

    protected Color Foreground { get; private set; }

    public double FrameCount => Duration.TotalSeconds * Framerate;
    public double Framerate => 60d;
    public TimeSpan Duration { get; }
    public IReadOnlyDictionary<string, double> Markers => EmptyMarkers;

    public double FrameToProgress(double frameNumber) => frameNumber / FrameCount;

    public IAnimatedVisual TryCreateAnimatedVisual(Compositor compositor)
    {
        var visual = CreateVisual(compositor);
        visual.CreateAnimations();
        return visual;
    }

    public IAnimatedVisual TryCreateAnimatedVisual(Compositor compositor, out object diagnostics)
    {
        diagnostics = null!;
        return TryCreateAnimatedVisual(compositor);
    }

    public void SetColorProperty(string propertyName, Color value)
    {
        if (string.Equals(propertyName, "Foreground", StringComparison.OrdinalIgnoreCase))
        {
            Foreground = value;
        }
    }

    public void SetScalarProperty(string propertyName, double value)
    {
    }

    protected abstract WinoCompositionAnimatedVisual CreateVisual(Compositor compositor);
}

public abstract partial class WinoCompositionAnimatedVisual : IAnimatedVisual, IAnimatedVisual2
{
    private readonly List<(CompositionObject Target, string Property)> _animatedProperties = [];
    private readonly AnimationController _animationController;
    private readonly ExpressionAnimation _progressExpression;
    private readonly List<CompositionBrush> _brushes = [];

    protected WinoCompositionAnimatedVisual(Compositor compositor, Color foreground, TimeSpan duration)
    {
        Compositor = compositor;
        Foreground = foreground;
        Duration = duration;
        Root = compositor.CreateContainerVisual();
        Root.Size = CanvasSize;
        Root.Properties.InsertScalar("Progress", 0f);

        _animationController = compositor.CreateAnimationController();
        _animationController.Pause();
        _progressExpression = compositor.CreateExpressionAnimation("root.Progress");
        _progressExpression.SetReferenceParameter("root", Root);
        _animationController.StartAnimation("Progress", _progressExpression);
    }

    protected static Vector2 CanvasSize => new(64f, 64f);
    protected Compositor Compositor { get; }
    protected Color Foreground { get; }
    protected ContainerVisual Root { get; }

    public Visual RootVisual => Root;
    public TimeSpan Duration { get; }
    public Vector2 Size => CanvasSize;

    public void CreateAnimations()
    {
        DestroyAnimations();
        StartSceneAnimations();
    }

    public void DestroyAnimations()
    {
        foreach (var (target, property) in _animatedProperties)
        {
            target.StopAnimation(property);
        }

        _animatedProperties.Clear();
    }

    public void Dispose()
    {
        DestroyAnimations();
        _animationController.StopAnimation("Progress");
        _progressExpression.Dispose();
        _animationController.Dispose();

        foreach (var brush in _brushes)
        {
            brush.Dispose();
        }

        Root.Dispose();
    }

    protected abstract void StartSceneAnimations();

    protected CompositionColorBrush Brush(float opacity = 1f)
    {
        var alpha = (byte)Math.Clamp((int)Math.Round(Foreground.A * opacity), 0, 255);
        return Brush(Color.FromArgb(alpha, Foreground.R, Foreground.G, Foreground.B));
    }

    protected CompositionColorBrush Brush(Color color)
    {
        var brush = Compositor.CreateColorBrush(color);
        _brushes.Add(brush);
        return brush;
    }

    protected ShapeVisual Rectangle(
        ContainerVisual parent,
        Vector2 size,
        Vector3 offset,
        float opacity = 1f,
        float cornerRadius = 0f)
    {
        var visual = Compositor.CreateShapeVisual();
        visual.Size = size;
        visual.Offset = offset;
        var geometry = Compositor.CreateRoundedRectangleGeometry();
        geometry.Size = size;
        geometry.CornerRadius = new Vector2(cornerRadius);
        var shape = Compositor.CreateSpriteShape(geometry);
        shape.FillBrush = Brush(opacity);
        visual.Shapes.Add(shape);
        parent.Children.InsertAtTop(visual);
        return visual;
    }

    protected ScalarKeyFrameAnimation ScalarAnimation(params (float Progress, float Value)[] keyFrames)
    {
        var animation = Compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = Duration;
        var easing = Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0.2f, 1f));

        foreach (var (progress, value) in keyFrames)
        {
            animation.InsertKeyFrame(progress, value, easing);
        }

        return animation;
    }

    protected Vector3KeyFrameAnimation OffsetAnimation(params (float Progress, Vector3 Value)[] keyFrames)
    {
        var animation = Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = Duration;
        var easing = Compositor.CreateLinearEasingFunction();

        foreach (var (progress, value) in keyFrames)
        {
            animation.InsertKeyFrame(progress, value, easing);
        }

        return animation;
    }

    protected Vector3KeyFrameAnimation ScaleAnimation(params (float Progress, Vector3 Value)[] keyFrames)
    {
        var animation = Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = Duration;
        var easing = Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0.2f, 1f));

        foreach (var (progress, value) in keyFrames)
        {
            animation.InsertKeyFrame(progress, value, easing);
        }

        return animation;
    }

    protected void Animate(CompositionObject target, string property, CompositionAnimation animation)
    {
        target.StartAnimation(property, animation, _animationController);
        _animatedProperties.Add((target, property));
    }
}

internal sealed partial class OrbitDotsAnimatedVisual : WinoCompositionAnimatedVisual
{
    private readonly ContainerVisual _orbit;

    public OrbitDotsAnimatedVisual(Compositor compositor, Color foreground, TimeSpan duration)
        : base(compositor, foreground, duration)
    {
        _orbit = compositor.CreateContainerVisual();
        _orbit.Size = CanvasSize;
        _orbit.CenterPoint = new Vector3(32f, 32f, 0f);
        Root.Children.InsertAtTop(_orbit);

        var ringVisual = compositor.CreateShapeVisual();
        ringVisual.Size = CanvasSize;
        var geometry = compositor.CreateEllipseGeometry();
        geometry.Center = new Vector2(32f, 32f);
        geometry.Radius = new Vector2(20f);
        var ring = compositor.CreateSpriteShape(geometry);
        ring.StrokeBrush = Brush(Color.FromArgb(0x47, 0x2D, 0x8E, 0xF4));
        ring.StrokeThickness = 2f;
        _orbit.Children.InsertAtBottom(ringVisual);
        ringVisual.Shapes.Add(ring);

        AddDot(new Vector3(28f, 8f, 0f), 8f, Color.FromArgb(0xFF, 0x2D, 0x8E, 0xF4));
        AddDot(new Vector3(48f, 36f, 0f), 7f, Color.FromArgb(0xFF, 0x2D, 0xBF, 0x75));
        AddDot(new Vector3(10f, 39f, 0f), 7f, Color.FromArgb(0xFF, 0x93, 0x5B, 0xEF));
    }

    protected override void StartSceneAnimations() =>
        Animate(_orbit, "RotationAngleInDegrees", ScalarAnimation((0f, 0f), (1f, 360f)));

    private void AddDot(Vector3 offset, float size, Color color)
    {
        var dot = Compositor.CreateShapeVisual();
        dot.Size = new Vector2(size);
        dot.Offset = offset;
        var geometry = Compositor.CreateEllipseGeometry();
        geometry.Center = new Vector2(size / 2f);
        geometry.Radius = new Vector2(size / 2f);
        var shape = Compositor.CreateSpriteShape(geometry);
        shape.FillBrush = Brush(color);
        dot.Shapes.Add(shape);
        _orbit.Children.InsertAtTop(dot);
        dot.CenterPoint = new Vector3(size / 2f, size / 2f, 0f);
    }
}

internal sealed partial class CubesAnimatedVisual : WinoCompositionAnimatedVisual
{
    private static readonly Vector3[] SnakePath =
    [
        new(8f, 23f, 0f), new(18f, 23f, 0f), new(28f, 23f, 0f), new(38f, 23f, 0f),
        new(48f, 23f, 0f), new(48f, 33f, 0f), new(38f, 33f, 0f), new(28f, 33f, 0f),
        new(18f, 33f, 0f), new(8f, 33f, 0f),
    ];

    private readonly List<ShapeVisual> _segments = [];

    public CubesAnimatedVisual(Compositor compositor, Color foreground, TimeSpan duration)
        : base(compositor, foreground, duration)
    {
        float[] opacities = [1f, 0.82f, 0.64f, 0.48f, 0.34f, 0.22f];

        for (var index = opacities.Length - 1; index >= 0; index--)
        {
            var size = index == 0 ? 9f : 8f;
            var segment = Rectangle(Root, new Vector2(size), SnakePath[(SnakePath.Length - index) % SnakePath.Length], opacities[index], 1.5f);
            _segments.Insert(0, segment);
        }
    }

    protected override void StartSceneAnimations()
    {
        for (var segmentIndex = 0; segmentIndex < _segments.Count; segmentIndex++)
        {
            var keyFrames = new (float Progress, Vector3 Value)[SnakePath.Length + 1];
            for (var step = 0; step <= SnakePath.Length; step++)
            {
                var pathIndex = (step - segmentIndex + SnakePath.Length) % SnakePath.Length;
                keyFrames[step] = ((float)step / SnakePath.Length, SnakePath[pathIndex]);
            }

            Animate(_segments[segmentIndex], "Offset", OffsetAnimation(keyFrames));
        }
    }
}

internal sealed partial class TranslateAnimatedVisual : WinoCompositionAnimatedVisual
{
    private readonly ContainerVisual _leftCard;
    private readonly ContainerVisual _rightCard;
    private readonly ShapeVisual _token;

    public TranslateAnimatedVisual(Compositor compositor, Color foreground, TimeSpan duration)
        : base(compositor, foreground, duration)
    {
        _leftCard = CreateLanguageCard(new Vector3(2f, 18f, 0f), false);
        _rightCard = CreateLanguageCard(new Vector3(42f, 18f, 0f), true);
        _token = Rectangle(Root, new Vector2(6f), new Vector3(22f, 22f, 0f), 1f, 2f);
        _token.Opacity = 0f;
    }

    protected override void StartSceneAnimations()
    {
        Animate(_token, "Offset", OffsetAnimation(
            (0f, new Vector3(22f, 22f, 0f)),
            (0.08f, new Vector3(22f, 22f, 0f)),
            (0.44f, new Vector3(36f, 22f, 0f)),
            (0.50f, new Vector3(36f, 22f, 0f)),
            (0.52f, new Vector3(36f, 36f, 0f)),
            (0.60f, new Vector3(36f, 36f, 0f)),
            (0.94f, new Vector3(22f, 36f, 0f)),
            (1f, new Vector3(22f, 36f, 0f))));
        Animate(_token, "Opacity", ScalarAnimation(
            (0f, 0f), (0.08f, 1f), (0.44f, 1f), (0.50f, 0f),
            (0.52f, 0f), (0.60f, 1f), (0.94f, 1f), (1f, 0f)));
        Animate(_rightCard, "Scale", ScaleAnimation(
            (0f, Vector3.One), (0.38f, Vector3.One), (0.48f, new Vector3(1.1f, 1.1f, 1f)), (0.58f, Vector3.One), (1f, Vector3.One)));
        Animate(_leftCard, "Scale", ScaleAnimation(
            (0f, Vector3.One), (0.88f, Vector3.One), (0.96f, new Vector3(1.1f, 1.1f, 1f)), (1f, Vector3.One)));
    }

    private ContainerVisual CreateLanguageCard(Vector3 offset, bool destination)
    {
        var card = Compositor.CreateContainerVisual();
        card.Size = new Vector2(20f, 28f);
        card.Offset = offset;
        card.CenterPoint = new Vector3(10f, 14f, 0f);
        Root.Children.InsertAtBottom(card);
        Rectangle(card, card.Size, Vector3.Zero, 0.18f, 4f);

        if (destination)
        {
            Rectangle(card, new Vector2(2.5f, 14f), new Vector3(5f, 7f, 0f), 0.8f, 1f);
            Rectangle(card, new Vector2(10f, 2.5f), new Vector3(5f, 7f, 0f), 0.8f, 1f);
            Rectangle(card, new Vector2(9f, 2.5f), new Vector3(5f, 13f, 0f), 0.58f, 1f);
            Rectangle(card, new Vector2(7f, 2.5f), new Vector3(5f, 19f, 0f), 0.4f, 1f);
        }
        else
        {
            Rectangle(card, new Vector2(12f, 2.5f), new Vector3(4f, 7f, 0f), 0.8f, 1f);
            Rectangle(card, new Vector2(9f, 2.5f), new Vector3(4f, 13f, 0f), 0.58f, 1f);
            Rectangle(card, new Vector2(12f, 2.5f), new Vector3(4f, 19f, 0f), 0.4f, 1f);
        }

        return card;
    }
}

internal sealed partial class SummarizeAnimatedVisual : WinoCompositionAnimatedVisual
{
    private readonly List<ShapeVisual> _sourceLines = [];
    private readonly ContainerVisual _resultCard;

    public SummarizeAnimatedVisual(Compositor compositor, Color foreground, TimeSpan duration)
        : base(compositor, foreground, duration)
    {
        float[] widths = [44f, 36f, 46f, 32f];
        for (var index = 0; index < widths.Length; index++)
        {
            _sourceLines.Add(Rectangle(Root, new Vector2(widths[index], 3f), new Vector3(9f, 10f + (index * 10f), 0f), 0.65f, 1.5f));
        }

        _resultCard = compositor.CreateContainerVisual();
        _resultCard.Size = new Vector2(38f, 22f);
        _resultCard.Offset = new Vector3(13f, 21f, 0f);
        _resultCard.Opacity = 0f;
        Root.Children.InsertAtTop(_resultCard);
        Rectangle(_resultCard, _resultCard.Size, Vector3.Zero, 0.2f, 5f);
        Rectangle(_resultCard, new Vector2(25f, 3f), new Vector3(6f, 7f, 0f), 0.9f, 1.5f);
        Rectangle(_resultCard, new Vector2(17f, 3f), new Vector3(6f, 13f, 0f), 0.56f, 1.5f);
    }

    protected override void StartSceneAnimations()
    {
        for (var index = 0; index < _sourceLines.Count; index++)
        {
            var line = _sourceLines[index];
            Animate(line, "Opacity", ScalarAnimation((0f, 0f), (0.08f, 0.65f), (0.34f, 0.65f), (0.55f, 0f), (1f, 0f)));
            Animate(line, "Scale", ScaleAnimation((0f, Vector3.One), (0.34f, Vector3.One), (0.55f, new Vector3(0.25f, 1f, 1f)), (1f, new Vector3(0.25f, 1f, 1f))));
        }

        Animate(_resultCard, "Opacity", ScalarAnimation((0f, 0f), (0.42f, 0f), (0.62f, 1f), (0.86f, 1f), (1f, 0f)));
        Animate(_resultCard, "Scale", ScaleAnimation(
            (0f, new Vector3(0.72f, 0.72f, 1f)),
            (0.42f, new Vector3(0.72f, 0.72f, 1f)),
            (0.64f, new Vector3(1.05f, 1.05f, 1f)),
            (0.74f, Vector3.One),
            (1f, Vector3.One)));
    }
}

internal sealed partial class RewriteAnimatedVisual : WinoCompositionAnimatedVisual
{
    private readonly ShapeVisual _firstRewrite;
    private readonly ShapeVisual _secondRewrite;
    private readonly ContainerVisual _pen;

    public RewriteAnimatedVisual(Compositor compositor, Color foreground, TimeSpan duration)
        : base(compositor, foreground, duration)
    {
        Rectangle(Root, new Vector2(44f, 3f), new Vector3(8f, 20f, 0f), 0.16f, 1.5f);
        Rectangle(Root, new Vector2(36f, 3f), new Vector3(8f, 36f, 0f), 0.16f, 1.5f);
        Rectangle(Root, new Vector2(27f, 3f), new Vector3(8f, 49f, 0f), 0.1f, 1.5f);

        _firstRewrite = Rectangle(Root, new Vector2(44f, 3f), new Vector3(8f, 20f, 0f), 0.92f, 1.5f);
        _firstRewrite.CenterPoint = Vector3.Zero;
        _firstRewrite.Scale = new Vector3(0.02f, 1f, 1f);
        _secondRewrite = Rectangle(Root, new Vector2(36f, 3f), new Vector3(8f, 36f, 0f), 0.72f, 1.5f);
        _secondRewrite.CenterPoint = Vector3.Zero;
        _secondRewrite.Scale = new Vector3(0.02f, 1f, 1f);

        _pen = compositor.CreateContainerVisual();
        _pen.Size = new Vector2(16f, 8f);
        _pen.Offset = new Vector3(4f, 10f, 0f);
        _pen.CenterPoint = new Vector3(8f, 4f, 0f);
        _pen.RotationAngleInDegrees = -24f;
        Root.Children.InsertAtTop(_pen);
        Rectangle(_pen, new Vector2(12f, 5f), Vector3.Zero, 1f, 2f);
        Rectangle(_pen, new Vector2(4f, 4f), new Vector3(11f, 0.5f, 0f), 0.55f, 1f);
        Rectangle(_pen, new Vector2(3f), new Vector3(14f, 1f, 0f), 0.95f, 1.5f);
    }

    protected override void StartSceneAnimations()
    {
        Animate(_firstRewrite, "Scale", ScaleAnimation(
            (0f, new Vector3(0.02f, 1f, 1f)),
            (0.08f, new Vector3(0.02f, 1f, 1f)),
            (0.43f, Vector3.One),
            (0.92f, Vector3.One),
            (1f, new Vector3(0.02f, 1f, 1f))));
        Animate(_secondRewrite, "Scale", ScaleAnimation(
            (0f, new Vector3(0.02f, 1f, 1f)),
            (0.50f, new Vector3(0.02f, 1f, 1f)),
            (0.87f, Vector3.One),
            (0.96f, Vector3.One),
            (1f, new Vector3(0.02f, 1f, 1f))));
        Animate(_pen, "Offset", OffsetAnimation(
            (0f, new Vector3(4f, 10f, 0f)),
            (0.08f, new Vector3(4f, 10f, 0f)),
            (0.43f, new Vector3(43f, 10f, 0f)),
            (0.50f, new Vector3(4f, 26f, 0f)),
            (0.87f, new Vector3(35f, 26f, 0f)),
            (0.94f, new Vector3(40f, 31f, 0f)),
            (1f, new Vector3(4f, 10f, 0f))));
        Animate(_pen, "Opacity", ScalarAnimation((0f, 0f), (0.05f, 1f), (0.92f, 1f), (0.98f, 0f), (1f, 0f)));
    }
}
