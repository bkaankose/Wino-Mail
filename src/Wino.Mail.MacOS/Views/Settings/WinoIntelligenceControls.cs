using AppKit;
using CoreGraphics;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>A Windows ToggleSwitch with custom On/Off content (Allowed / Not allowed): label, then NSSwitch.</summary>
public sealed class WinoTextSwitch : NSView
{
    private readonly NSTextField _label;
    private readonly string _on;
    private readonly string _off;

    public WinoTextSwitch(string on, string off, string accessibilityLabel)
    {
        _on = on;
        _off = off;
        TranslatesAutoresizingMaskIntoConstraints = false;
        Switch = new NSSwitch { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(Switch, accessibilityLabel);
        _label = WinoStyle.Label(off, WinoStyle.Body, WinoStyle.SecondaryText);
        _label.Alignment = NSTextAlignment.Right;
        double Measure(string text) => new Foundation.NSAttributedString(text, new NSStringAttributes { Font = WinoStyle.Body }).Size.Width;
        _label.WidthAnchor.ConstraintGreaterThanOrEqualTo((nfloat)Math.Ceiling(Math.Max(Measure(on), Measure(off)))).Active = true;
        WinoLayout.Fill(WinoLayout.HStack(8, _label, Switch), this);
        Switch.Activated += (_, _) => { Update(); Toggled?.Invoke(this, EventArgs.Empty); };
        Update();
    }

    public NSSwitch Switch { get; }
    public event EventHandler? Toggled;

    public bool IsOn
    {
        get => Switch.State != 0;
        set { Switch.State = value ? 1 : 0; Update(); }
    }

    public bool IsEnabled
    {
        get => Switch.Enabled;
        set { Switch.Enabled = value; _label.AlphaValue = value ? 1 : 0.55f; }
    }

    private void Update() => _label.StringValue = IsOn ? _on : _off;
}

/// <summary>A thin rounded progress bar (Windows ProgressBar, 4pt) with an error tint.</summary>
public sealed class WinoBarView : NSView
{
    private double _value;
    private bool _error;
    private NSColor? _tint;

    public WinoBarView(double width = -1, double height = 4)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Size(this, width, height);
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.ProgressIndicatorRole;
    }

    /// <summary>0 to 100.</summary>
    public double Value { get => _value; set { _value = Math.Clamp(value, 0, 100); AccessibilityValue = new Foundation.NSNumber(_value); NeedsDisplay = true; } }
    public bool ShowError { get => _error; set { _error = value; NeedsDisplay = true; } }
    public NSColor? Tint { get => _tint; set { _tint = value; NeedsDisplay = true; } }

    public override void ViewDidChangeEffectiveAppearance() { base.ViewDidChangeEffectiveAppearance(); NeedsDisplay = true; }

    public override void DrawRect(CGRect dirtyRect)
    {
        var radius = Bounds.Height / 2;
        WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.10), WinoStyle.Hex(0xFFFFFF, 0.14)).SetFill();
        NSBezierPath.FromRoundedRect(Bounds, radius, radius).Fill();
        if (_value <= 0) return;
        (_error ? WinoStyle.Critical : _tint ?? WinoStyle.Accent).SetFill();
        var fill = new CGRect(0, 0, Bounds.Width * _value / 100, Bounds.Height);
        NSBezierPath.FromRoundedRect(fill, radius, radius).Fill();
    }
}

/// <summary>The "Available quota" flyout: one row per usage bucket (name, value, bar) and the reset line.</summary>
public sealed class IntelligenceQuotaViewController(IReadOnlyList<IntelligenceUsageItem> items, string resetText) : NSViewController
{
    public override void LoadView()
    {
        var stack = WinoLayout.VStack(8);
        stack.Alignment = NSLayoutAttribute.Leading;
        stack.EdgeInsets = new NSEdgeInsets(14, 16, 12, 16);
        foreach (var item in items)
        {
            var name = WinoStyle.Label(item.DisplayName, WinoStyle.Body, WinoStyle.PrimaryText);
            name.LineBreakMode = NSLineBreakMode.TruncatingTail;
            var value = WinoStyle.Label(item.Value, WinoStyle.Body, WinoStyle.SecondaryText);
            var top = WinoLayout.HStack(8, name, WinoLayout.Spacer(), value);
            var bar = new WinoBarView { Value = item.Percentage, ShowError = item.IsExhausted };
            WinoAccessibility.Label(bar, item.DisplayName);
            var row = WinoLayout.VStack(4, top, bar);
            row.Alignment = NSLayoutAttribute.Leading;
            top.WidthAnchor.ConstraintEqualTo(row.WidthAnchor).Active = true;
            bar.WidthAnchor.ConstraintEqualTo(row.WidthAnchor).Active = true;
            stack.AddArrangedSubview(row);
            row.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor, 1, -32).Active = true;
        }
        if (!string.IsNullOrWhiteSpace(resetText))
            stack.AddArrangedSubview(WinoStyle.Label(resetText, NSFont.SystemFontOfSize(12), WinoStyle.SecondaryText, 0));
        stack.WidthAnchor.ConstraintEqualTo(352).Active = true;
        View = stack;
    }
}

/// <summary>Small factory helpers shared by the Intelligence pages.</summary>
internal static class IntelligenceViews
{
    public static NSProgressIndicator Spinner(double size = 16)
    {
        var spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, Indeterminate = true, IsDisplayedWhenStopped = false, ControlSize = NSControlSize.Small, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(spinner, size, size);
        return spinner;
    }

    public static void SetSpinning(NSProgressIndicator spinner, bool spinning)
    {
        spinner.Hidden = !spinning;
        if (spinning) spinner.StartAnimation(null); else spinner.StopAnimation(null);
    }

    /// <summary>The page-level first-load state: a centred spinner shown until cached content is ready.</summary>
    public static NSView LoadingState(out NSProgressIndicator spinner)
    {
        spinner = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, Indeterminate = true, IsDisplayedWhenStopped = false, ControlSize = NSControlSize.Regular, TranslatesAutoresizingMaskIntoConstraints = false };
        var host = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        host.AddSubview(spinner);
        NSLayoutConstraint.ActivateConstraints(
        [
            spinner.CenterXAnchor.ConstraintEqualTo(host.CenterXAnchor),
            spinner.TopAnchor.ConstraintEqualTo(host.TopAnchor, 48),
            spinner.BottomAnchor.ConstraintEqualTo(host.BottomAnchor, -48)
        ]);
        spinner.StartAnimation(null);
        return host;
    }

    /// <summary>
    /// Visibility for a transient progress row: it appears only when the work outlasts
    /// <paramref name="delay"/> seconds (quick refreshes never shift the layout) and hides as soon as
    /// the work ends. <paramref name="alive"/> stops a pending show after the page is released.
    /// </summary>
    public static Action<bool> DelayedProgressRow(NSView row, NSProgressIndicator spinner, Func<bool> alive, double delay = 0.6)
    {
        var generation = 0;
        row.Hidden = true;
        SetSpinning(spinner, false);
        return on =>
        {
            if (on && !row.Hidden) return;
            var current = ++generation;
            if (!on)
            {
                row.Hidden = true;
                SetSpinning(spinner, false);
                return;
            }
            var timer = NSTimer.CreateTimer(delay, false, _ =>
            {
                if (current != generation || !alive()) return;
                row.Hidden = false;
                SetSpinning(spinner, true);
            });
            // Common modes: the row still appears while the user scrolls or drags.
            Foundation.NSRunLoop.Main.AddTimer(timer, Foundation.NSRunLoopMode.Common);
        };
    }

    /// <summary>A card surface like the Windows brand promo (settings card fill, stroke, radius 6, padding 20).</summary>
    public static WinoSurfaceView Surface(NSView content, double padding = 20, double radius = 6)
    {
        var surface = new WinoSurfaceView
        {
            Fill = Wino.Mail.Controls.AppKit.Settings.WinoSettingsStyle.CardFill,
            Stroke = Wino.Mail.Controls.AppKit.Settings.WinoSettingsStyle.CardStroke,
            CornerRadius = radius
        };
        WinoLayout.Fill(content, surface, padding);
        return surface;
    }

    /// <summary>Wrapping flow of fixed-height chips (Windows WrapPanel): a vertical stack of rows rebuilt to the width.</summary>
    public static NSView Flow(IReadOnlyList<NSView> items, double spacing, double width)
    {
        var rows = WinoLayout.VStack(spacing);
        rows.Alignment = NSLayoutAttribute.Leading;
        NSStackView? row = null;
        double used = 0;
        foreach (var item in items)
        {
            var w = item.FittingSize.Width;
            if (row is null || used + w > width)
            {
                row = WinoLayout.HStack(spacing);
                rows.AddArrangedSubview(row);
                used = 0;
            }
            row.AddArrangedSubview(item);
            used += w + spacing;
        }
        return rows;
    }
}
