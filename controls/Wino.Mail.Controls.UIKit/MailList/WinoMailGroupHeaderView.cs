using Foundation;
using UIKit;
using Wino.Mail.Controls.Core;
using Wino.Presentation;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.MailList;

/// <summary>Colours of the mail list that follow the Wino theme (the Windows MailListHeaderBackgroundColor).</summary>
public static class WinoMailListTheme
{
    /// <summary>
    /// Date group header strip: with a theme backdrop the accent tinted on white in light and white at 7% in dark; the
    /// Default theme uses the Windows Default values; a custom theme's MailListHeader surface wins.
    /// </summary>
    public static UIColor GroupHeaderFill()
        => UIColor.FromDynamicProvider(traits =>
        {
            bool dark = traits.UserInterfaceStyle == UIUserInterfaceStyle.Dark;
            if (WinoThemeSurfaces.Resolve(WinoThemeSurface.MailListHeader, dark) is { } custom) return custom;
            if (!WinoStyle.HasBackdrop) return dark ? WinoStyle.Hex(0x2C2C2C) : WinoStyle.Hex(0xECF0F1);
            if (dark) return UIColor.White.ColorWithAlpha(0.07f);
            return Blend(UIColor.White, WinoStyle.Accent.GetResolvedColor(traits), 0.3);
        });

    /// <summary>What a sticky header paints under its strip so the rows beneath do not show through.</summary>
    public static UIColor FloatingGroupBackground => WinoStyle.Dynamic(UIColor.White, WinoStyle.Hex(0x343438, 0.96));

    private static UIColor Blend(UIColor from, UIColor to, double fraction)
    {
        from.GetRGBA(out var r1, out var g1, out var b1, out var a1);
        to.GetRGBA(out var r2, out var g2, out var b2, out var a2);
        nfloat Mix(nfloat a, nfloat b) => (nfloat)(a + (b - a) * fraction);
        return UIColor.FromRGBA(Mix(r1, r2), Mix(g1, g2), Mix(b1, b2), Mix(a1, a2));
    }
}

/// <summary>
/// Date group header ("Today", "Yesterday"; UIKit twin of the AppKit <c>WinoMailGroupHeaderView</c>): a full-width strip
/// with 6pt radius, 7×12 padding, semibold text on the theme's header colour and a 6pt gap below, aligned with the row
/// content box (Windows MailGroupHeaderDefaultTemplate). Use it as a table section header; the table pins it while
/// its section scrolls, and <see cref="WinoMailRowMeasure.GroupRowHeight"/> is its height.
/// </summary>
public class WinoMailGroupHeaderView : UITableViewHeaderFooterView
{
    public new const string ReuseIdentifier = "WinoMailGroupHeader";

    private readonly WinoSurfaceView _strip;
    private readonly UILabel _title;

    public WinoMailGroupHeaderView() : this(new NSString(ReuseIdentifier)) { }

    [Export("initWithReuseIdentifier:")]
    public WinoMailGroupHeaderView(NSString reuseIdentifier) : base(reuseIdentifier)
    {
        var background = new UIView { BackgroundColor = WinoMailListTheme.FloatingGroupBackground };
        BackgroundView = background;
        _strip = new WinoSurfaceView { CornerRadius = 6, Fill = WinoMailListTheme.GroupHeaderFill() };
        _title = WinoStyle.Label(string.Empty, WinoMailRowMeasure.Font(WinoMailRowMetrics.GroupHeaderFontSize, UIFontWeight.Semibold), WinoStyle.PrimaryText);
        ContentView.AddSubview(_strip);
        _strip.AddSubview(_title);
        NSLayoutConstraint.ActivateConstraints(
        [
            _strip.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, (nfloat)WinoMailRowMetrics.ContentLeading),
            _strip.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -(nfloat)WinoMailRowMetrics.ContentTrailing),
            _strip.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor),
            _strip.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -6),
            _title.LeadingAnchor.ConstraintEqualTo(_strip.LeadingAnchor, 12),
            _title.TrailingAnchor.ConstraintLessThanOrEqualTo(_strip.TrailingAnchor, -12),
            _title.CenterYAnchor.ConstraintEqualTo(_strip.CenterYAnchor)
        ]);
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.Header;
    }

    /// <summary>Re-reads the theme colour (accent or backdrop changed).</summary>
    public void RefreshTheme() => _strip.Fill = WinoMailListTheme.GroupHeaderFill();

    public string Title
    {
        get => _title.Text ?? string.Empty;
        set { _title.Text = value ?? string.Empty; AccessibilityLabel = value; }
    }
}
