using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.VoiceBubble;

/// <summary>
/// The voice bars: 12 bars beside the icon, or 40 when the transcript widens the bubble. Bars are layers scaled around
/// their centre, so a new level never triggers layout; the wide waveform mirrors the bands around its centre.
/// </summary>
internal sealed class VoiceBubbleBarsView : NSView
{
    private const int CompactCount = 12;
    private const int WideCount = 40;
    private const double CompactSpacing = 2.33;
    private const double WideSpacing = 3.4;
    private const double BarWidth = 3;
    private const double RestHeight = 3;
    private const double Range = 20;
    private const double FullHeight = 23;
    private const double ViewHeight = 24;

    public static readonly NSColor BarColor = WinoStyle.Dynamic(WinoStyle.Hex(0x0070D8), WinoStyle.Hex(0x00A8F8));

    private readonly CALayer[] _bars = new CALayer[WideCount];
    private NSLayoutConstraint _width;
    private int _visibleCount = CompactCount;

    public VoiceBubbleBarsView()
    {
        WantsLayer = true;
        TranslatesAutoresizingMaskIntoConstraints = false;
        for (var index = 0; index < _bars.Length; index++)
        {
            _bars[index] = new CALayer { CornerRadius = (nfloat)(BarWidth / 2) };
            Layer!.AddSublayer(_bars[index]);
        }

        _width = WidthAnchor.ConstraintEqualTo((nfloat)TrackWidth(CompactCount, CompactSpacing));
        _width.Active = true;
        HeightAnchor.ConstraintEqualTo((nfloat)ViewHeight).Active = true;
        ApplyColors();
        LayoutBars();
        Reset();
    }

    public override bool IsFlipped => true;

    public void SetWide(bool wide)
    {
        var count = wide ? WideCount : CompactCount;
        if (count == _visibleCount)
            return;

        _visibleCount = count;
        _width.Constant = (nfloat)TrackWidth(count, wide ? WideSpacing : CompactSpacing);
        LayoutBars();
        Reset();
    }

    public void Reset()
    {
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        foreach (var bar in _bars)
            SetBar(bar, 0);
        CATransaction.Commit();
    }

    public void SetBands(ReadOnlySpan<float> bands)
    {
        if (bands.Length == 0)
            return;

        CATransaction.Begin();
        CATransaction.DisableActions = true;
        var wide = _visibleCount > CompactCount;
        for (var index = 0; index < _visibleCount; index++)
        {
            var position = wide
                ? Math.Abs(index - ((_visibleCount - 1) / 2.0)) / ((_visibleCount - 1) / 2.0)
                : index / (double)_visibleCount;
            var bandIndex = Math.Min(bands.Length - 1, (int)(position * bands.Length));
            SetBar(_bars[index], Math.Clamp(bands[bandIndex], 0f, 1f));
        }

        CATransaction.Commit();
    }

    public void ApplyColors()
    {
        CGColor? color = null;
        EffectiveAppearance.PerformAsCurrentDrawingAppearance(() => color = BarColor.CGColor);
        foreach (var bar in _bars)
            bar.BackgroundColor = color;
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        ApplyColors();
    }

    private void LayoutBars()
    {
        var spacing = _visibleCount > CompactCount ? WideSpacing : CompactSpacing;
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        for (var index = 0; index < _bars.Length; index++)
        {
            var bar = _bars[index];
            bar.Hidden = index >= _visibleCount;
            bar.Bounds = new CGRect(0, 0, BarWidth, FullHeight);
            bar.Position = new CGPoint((index * (BarWidth + spacing)) + (BarWidth / 2), ViewHeight / 2);
        }

        CATransaction.Commit();
    }

    private static void SetBar(CALayer bar, float band)
    {
        bar.Transform = CATransform3D.MakeScale(1, (nfloat)((RestHeight + (band * Range)) / FullHeight), 1);
        bar.Opacity = 0.45f + (0.55f * band);
    }

    private static double TrackWidth(int count, double spacing) => (count * BarWidth) + ((count - 1) * spacing) + 2;
}

/// <summary>Three dots that rise and brighten in turn while the plan is prepared.</summary>
internal sealed class VoiceBubbleDotsView : NSView
{
    private const double DotSize = 6;
    private const double Spacing = 5;
    private const float RestOpacity = 0.45f;

    private readonly CALayer[] _dots = new CALayer[3];

    public VoiceBubbleDotsView()
    {
        WantsLayer = true;
        TranslatesAutoresizingMaskIntoConstraints = false;
        for (var index = 0; index < _dots.Length; index++)
        {
            _dots[index] = new CALayer
            {
                Bounds = new CGRect(0, 0, DotSize, DotSize),
                CornerRadius = (nfloat)(DotSize / 2),
                Opacity = RestOpacity,
                Position = new CGPoint(4 + (DotSize / 2) + (index * (DotSize + Spacing)), 12)
            };
            Layer!.AddSublayer(_dots[index]);
        }

        WidthAnchor.ConstraintEqualTo((nfloat)(4 + (3 * DotSize) + (2 * Spacing) + 6)).Active = true;
        HeightAnchor.ConstraintEqualTo(24).Active = true;
        ApplyColors();
    }

    public override bool IsFlipped => true;

    public void Start()
    {
        var now = CAAnimation.CurrentMediaTime();
        for (var index = 0; index < _dots.Length; index++)
        {
            var dot = _dots[index];
            if (dot.AnimationForKey("bounce") is not null)
                continue;

            var rise = CAKeyFrameAnimation.FromKeyPath("transform.translation.y");
            rise.Values = [NSNumber.FromDouble(0), NSNumber.FromDouble(-5), NSNumber.FromDouble(0), NSNumber.FromDouble(0)];
            rise.KeyTimes = [NSNumber.FromDouble(0), NSNumber.FromDouble(0.4), NSNumber.FromDouble(0.8), NSNumber.FromDouble(1)];
            rise.TimingFunctions = [CAMediaTimingFunction.FromControlPoints(0.37f, 0f, 0.63f, 1f), CAMediaTimingFunction.FromControlPoints(0.37f, 0f, 0.63f, 1f), CAMediaTimingFunction.FromName(CAMediaTimingFunction.Linear)];

            var glow = CAKeyFrameAnimation.FromKeyPath("opacity");
            glow.Values = [NSNumber.FromFloat(RestOpacity), NSNumber.FromFloat(1), NSNumber.FromFloat(RestOpacity), NSNumber.FromFloat(RestOpacity)];
            glow.KeyTimes = rise.KeyTimes;

            var group = new CAAnimationGroup
            {
                Animations = [rise, glow],
                Duration = 1,
                RepeatCount = float.PositiveInfinity,
                BeginTime = now + (index * 0.15)
            };
            dot.AddAnimation(group, "bounce");
        }
    }

    public void Stop()
    {
        foreach (var dot in _dots)
            dot.RemoveAnimation("bounce");
    }

    public void ApplyColors()
    {
        CGColor? color = null;
        EffectiveAppearance.PerformAsCurrentDrawingAppearance(() => color = VoiceBubbleBarsView.BarColor.CGColor);
        foreach (var dot in _dots)
            dot.BackgroundColor = color;
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        ApplyColors();
    }
}
