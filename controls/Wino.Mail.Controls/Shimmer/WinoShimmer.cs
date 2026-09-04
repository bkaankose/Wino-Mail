using System.Numerics;
using CommunityToolkit.WinUI;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Wino.Mail.Controls.Shimmer;

/// <summary>
/// A skeleton placeholder that sweeps a highlight across its surface while content loads.
/// </summary>
/// <remarks>
/// The sweep is a composition sprite layered over the control's own background, so the control costs
/// one visual and no layout passes. Hosts size it like any other block and give it the corner radius
/// of the element it stands in for.
/// </remarks>
public sealed partial class WinoShimmer : Control
{
    private const string ShimmerRootPartName = "PART_ShimmerRoot";
    private const string SweepAnimationName = "Offset.X";

    private readonly UISettings _uiSettings = new();

    private Border? _shimmerRoot;
    private Visual? _rootVisual;
    private CompositionRoundedRectangleGeometry? _clipGeometry;
    private SpriteVisual? _sweepVisual;
    private CompositionLinearGradientBrush? _sweepBrush;
    private CompositionColorGradientStop? _baseStartStop;
    private CompositionColorGradientStop? _highlightStop;
    private CompositionColorGradientStop? _baseEndStop;
    private bool _isSweeping;
    private float _sweepWidth;

    /// <summary>Gets or sets a value indicating whether the highlight sweep runs.</summary>
    [GeneratedDependencyProperty(DefaultValue = true)]
    public partial bool IsActive { get; set; }

    /// <summary>Gets or sets the duration of a single sweep, in milliseconds.</summary>
    [GeneratedDependencyProperty(DefaultValue = 1600)]
    public partial int SweepDurationMilliseconds { get; set; }

    /// <summary>Gets or sets the brush the highlight is drawn with.</summary>
    /// <remarks>
    /// Composition needs a <see cref="Color"/>, and theme resources declared in a control library's
    /// Generic.xaml cannot be resolved from code, so the default style hands this over as a
    /// <see cref="ThemeResource"/>-bound property instead.
    /// </remarks>
    [GeneratedDependencyProperty]
    public partial Brush? HighlightBrush { get; set; }

    /// <summary>Initializes a new instance of the <see cref="WinoShimmer"/> class.</summary>
    public WinoShimmer()
    {
        DefaultStyleKey = typeof(WinoShimmer);

        RegisterPropertyChangedCallback(IsActiveProperty, OnSweepPropertyChanged);
        RegisterPropertyChangedCallback(SweepDurationMillisecondsProperty, OnSweepPropertyChanged);
        RegisterPropertyChangedCallback(HighlightBrushProperty, OnColorPropertyChanged);
        RegisterPropertyChangedCallback(BackgroundProperty, OnColorPropertyChanged);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        ActualThemeChanged += OnActualThemeChanged;
    }

    protected override void OnApplyTemplate()
    {
        StopSweep();
        DetachSweepVisual();

        base.OnApplyTemplate();

        _shimmerRoot = GetTemplateChild(ShimmerRootPartName) as Border;

        UpdateSweep();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateSweep();

    // The reading pane keeps its page alive across messages, so a forever-looping composition
    // animation on an unloaded skeleton would simply keep running.
    private void OnUnloaded(object sender, RoutedEventArgs e) => StopSweep();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateSweep();

    private void OnActualThemeChanged(FrameworkElement sender, object args) => UpdateSweep();

    private void OnSweepPropertyChanged(DependencyObject sender, DependencyProperty property) => UpdateSweep();

    private void OnColorPropertyChanged(DependencyObject sender, DependencyProperty property) => UpdateSweep();

    private bool CanSweep => IsActive
                             && _uiSettings.AnimationsEnabled
                             && ActualWidth > 0
                             && ActualHeight > 0;

    private void UpdateSweep()
    {
        if (_shimmerRoot is null) return;

        if (!CanSweep)
        {
            StopSweep();
            return;
        }

        EnsureSweepVisual();

        if (_sweepVisual is null) return;

        var size = new Vector2((float)ActualWidth, (float)ActualHeight);

        _sweepVisual.Size = size;
        UpdateClip(size);
        UpdateSweepColors();
        StartSweep();
    }

    private void EnsureSweepVisual()
    {
        if (_sweepVisual is not null || _shimmerRoot is null) return;

        _rootVisual = ElementCompositionPreview.GetElementVisual(_shimmerRoot);

        var compositor = _rootVisual.Compositor;

        // The highlight travels a full width past each edge, and a composition child visual is not
        // bounded by the XAML element that hosts it. Clipping the host visual keeps the sweep inside
        // the placeholder, corner radius included.
        _clipGeometry = compositor.CreateRoundedRectangleGeometry();
        _rootVisual.Clip = compositor.CreateGeometricClip(_clipGeometry);

        _baseStartStop = compositor.CreateColorGradientStop(0f, Colors.Transparent);
        _highlightStop = compositor.CreateColorGradientStop(0.5f, Colors.Transparent);
        _baseEndStop = compositor.CreateColorGradientStop(1f, Colors.Transparent);

        _sweepBrush = compositor.CreateLinearGradientBrush();
        _sweepBrush.StartPoint = new Vector2(0f, 0f);
        _sweepBrush.EndPoint = new Vector2(1f, 0f);
        _sweepBrush.ColorStops.Add(_baseStartStop);
        _sweepBrush.ColorStops.Add(_highlightStop);
        _sweepBrush.ColorStops.Add(_baseEndStop);

        _sweepVisual = compositor.CreateSpriteVisual();
        _sweepVisual.Brush = _sweepBrush;

        ElementCompositionPreview.SetElementChildVisual(_shimmerRoot, _sweepVisual);
    }

    private void UpdateClip(Vector2 size)
    {
        if (_clipGeometry is null) return;

        _clipGeometry.Size = size;
        _clipGeometry.CornerRadius = new Vector2((float)CornerRadius.TopLeft, (float)CornerRadius.TopLeft);
    }

    private void UpdateSweepColors()
    {
        if (_baseStartStop is null || _highlightStop is null || _baseEndStop is null) return;

        var highlight = ResolveColor(HighlightBrush);

        // The sprite sits on top of the control's own background, so the ends of the gradient fade
        // out rather than repainting the base colour.
        var transparentHighlight = Color.FromArgb(0, highlight.R, highlight.G, highlight.B);

        _baseStartStop.Color = transparentHighlight;
        _highlightStop.Color = highlight;
        _baseEndStop.Color = transparentHighlight;
    }

    private void StartSweep()
    {
        if (_sweepVisual is null) return;

        var width = (float)ActualWidth;

        // A resize changes how far the highlight has to travel, so an already running sweep has to
        // be rebuilt rather than left on the old range.
        if (_isSweeping && width == _sweepWidth) return;

        var compositor = _sweepVisual.Compositor;

        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0f, -width, compositor.CreateLinearEasingFunction());
        animation.InsertKeyFrame(1f, width, compositor.CreateLinearEasingFunction());
        animation.Duration = TimeSpan.FromMilliseconds(Math.Max(1, SweepDurationMilliseconds));
        animation.IterationBehavior = AnimationIterationBehavior.Forever;

        _sweepVisual.IsVisible = true;
        _sweepVisual.StartAnimation(SweepAnimationName, animation);
        _isSweeping = true;
        _sweepWidth = width;
    }

    private void StopSweep()
    {
        if (_sweepVisual is null) return;

        _sweepVisual.StopAnimation(SweepAnimationName);
        _sweepVisual.Offset = Vector3.Zero;

        // Nothing should linger over the flat base fill once the sweep is off, which is also the
        // rendering used when the user has turned animations off in Windows.
        _sweepVisual.IsVisible = false;
        _isSweeping = false;
    }

    private void DetachSweepVisual()
    {
        if (_shimmerRoot is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(_shimmerRoot, null);
        }

        if (_rootVisual is not null)
        {
            _rootVisual.Clip = null;
            _rootVisual = null;
        }

        _clipGeometry = null;
        _sweepVisual = null;
        _sweepBrush = null;
        _baseStartStop = null;
        _highlightStop = null;
        _baseEndStop = null;
    }

    private Color ResolveColor(Brush? brush) => brush is SolidColorBrush solidColorBrush
        ? solidColorBrush.Color
        : Colors.Transparent;
}
