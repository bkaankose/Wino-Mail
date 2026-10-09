using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.MailList;

/// <summary>
/// Row container for <see cref="WinoMailRowView"/>. Selection is the Windows look: a subtle
/// fill on the content box (inset by the row gap, 6pt radius) with a 3pt accent pill inside its
/// leading edge. In select mode checked rows get a 12% accent tint instead. Hover is tracked here
/// and forwarded to the cell so the hover actions appear at the trailing edge.
/// </summary>
public class WinoMailTableRowView : NSTableRowView
{
    public const string ReuseIdentifier = "WinoMailTableRow";

    private NSTrackingArea? _tracking;
    private bool _hovered;

    public WinoMailTableRowView() => Identifier = ReuseIdentifier;

    /// <summary>Select mode draws a tint instead of the pill.</summary>
    public bool IsSelectionMode { get; set; }

    public bool IsHovered => _hovered;

    /// <summary>Selection never inverts the content: the cell keeps its normal colours.</summary>
    public override NSBackgroundStyle InteriorBackgroundStyle => NSBackgroundStyle.Normal;

    public override bool Selected
    {
        get => base.Selected;
        set
        {
            base.Selected = value;
            if (Cell is { } cell) cell.IsChecked = value;
            NeedsDisplay = true;
        }
    }

    private WinoMailRowView? Cell => NumberOfColumns > 0 ? ViewAtColumn(0) as WinoMailRowView : null;

    private double Indent
    {
        get
        {
            var model = Cell?.Model;
            return model is { Kind: WinoMailRowKind.ThreadChild } ? WinoMailRowMetrics.ChildIndent(model.Density) : 0;
        }
    }

    /// <summary>The highlight box; the cell lays its content out inside the same rectangle.</summary>
    private CGRect ContentBox
        => new((nfloat)(WinoMailRowMetrics.ContentLeading + Indent), (nfloat)WinoMailRowMetrics.RowGap,
            Bounds.Width - (nfloat)(WinoMailRowMetrics.ContentLeading + Indent + WinoMailRowMetrics.ContentTrailing),
            Bounds.Height - (nfloat)(2 * WinoMailRowMetrics.RowGap));

    private void FillBox(NSColor color)
    {
        color.SetFill();
        NSBezierPath.FromRoundedRect(ContentBox, 6, 6).Fill();
    }

    public override void DrawSelection(CGRect dirtyRect)
    {
        if (IsSelectionMode)
        {
            FillBox(WinoStyle.Accent.ColorWithAlphaComponent((nfloat)0.12));
            return;
        }
        FillBox(WinoStyle.Dynamic(NSColor.Black.ColorWithAlphaComponent((nfloat)0.06), NSColor.White.ColorWithAlphaComponent((nfloat)0.10)));
        WinoStyle.Accent.SetFill();
        // Windows ListViewItem pill: inside the fill, vertically centred, shorter than the row.
        var box = ContentBox;
        var pillHeight = (nfloat)Math.Clamp((double)box.Height - 28, 16, 40);
        var pill = new CGRect(box.X + (nfloat)WinoMailRowMetrics.PillInset, box.Y + (box.Height - pillHeight) / 2, (nfloat)WinoMailRowMetrics.PillWidth, pillHeight);
        NSBezierPath.FromRoundedRect(pill, (nfloat)(WinoMailRowMetrics.PillWidth / 2), (nfloat)(WinoMailRowMetrics.PillWidth / 2)).Fill();
    }

    public override void DrawBackground(CGRect dirtyRect)
    {
        if (_hovered && !Selected) FillBox(NSColor.Label.ColorWithAlphaComponent((nfloat)0.045));
    }

    public override void DrawSeparator(CGRect dirtyRect)
    {
    }

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        if (_tracking is not null)
        {
            // Called on every scroll and resize: release the replaced area instead of leaving it to the GC.
            RemoveTrackingArea(_tracking);
            _tracking.Dispose();
        }
        _tracking = new NSTrackingArea(Bounds,
            NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect,
            this, null);
        AddTrackingArea(_tracking);
    }

    public override void MouseEntered(NSEvent theEvent) => SetHovered(true);

    public override void MouseExited(NSEvent theEvent) => SetHovered(false);

    /// <summary>Debug and tests: forces the hover state without a pointer.</summary>
    public void SetHovered(bool hovered)
    {
        if (_hovered == hovered) return;
        _hovered = hovered;
        if (Cell is { } cell) cell.IsHovered = hovered;
        NeedsDisplay = true;
    }

    public override void DidAddSubview(NSView subview)
    {
        base.DidAddSubview(subview);
        if (subview is WinoMailRowView cell)
        {
            cell.IsHovered = _hovered;
            cell.IsChecked = Selected;
        }
    }

    public override void PrepareForReuse()
    {
        base.PrepareForReuse();
        _hovered = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _tracking is not null)
        {
            RemoveTrackingArea(_tracking);
            _tracking.Dispose();
            _tracking = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>Colours of the mail list that follow the Wino theme (the Windows MailListHeaderBackgroundColor).</summary>
public static class WinoMailListTheme
{
    /// <summary>
    /// Date group header strip: with a theme backdrop the accent tinted on white in light
    /// (Clouds #0984e3 gives #b2dffc) and white at 7% in dark; the Default theme uses the
    /// Windows Default values.
    /// </summary>
    public static NSColor GroupHeaderFill()
        => NSColor.GetColor(string.Empty, appearance =>
        {
            bool dark = appearance.FindBestMatch([NSAppearance.NameAqua, NSAppearance.NameDarkAqua]) == NSAppearance.NameDarkAqua;
            if (WinoThemeSurfaces.Resolve(WinoThemeSurface.MailListHeader, dark) is { } custom) return custom;
            if (!WinoStyle.HasBackdrop) return dark ? WinoStyle.Hex(0x2C2C2C) : WinoStyle.Hex(0xECF0F1);
            if (dark) return NSColor.White.ColorWithAlphaComponent((nfloat)0.07);
            var accent = WinoStyle.Accent.UsingColorSpace(NSColorSpace.SRGBColorSpace) ?? WinoStyle.Accent;
            return NSColor.White.UsingColorSpace(NSColorSpace.SRGBColorSpace)!.BlendedColor((nfloat)0.3, accent) ?? accent;
        });

    /// <summary>What a floating group row paints under its strip so the rows beneath do not show through.</summary>
    public static NSColor FloatingGroupBackground => WinoStyle.Dynamic(NSColor.White, WinoStyle.Hex(0x343438, 0.96));
}

/// <summary>
/// Sticky date group header ("Today", "Yesterday"): a full-width strip with 6pt radius, 7×12
/// padding, 11pt semibold text on the theme's header colour, 6pt gap below, aligned with the
/// row content box (Windows MailGroupHeaderDefaultTemplate).
/// </summary>
public class WinoMailGroupHeaderView : NSTableCellView
{
    public const string ReuseIdentifier = "WinoMailGroupHeader";
    private readonly WinoSurfaceView _strip;
    private readonly NSTextField _title;

    public WinoMailGroupHeaderView()
    {
        Identifier = ReuseIdentifier;
        _strip = new WinoSurfaceView { CornerRadius = 6, Fill = WinoMailListTheme.GroupHeaderFill() };
        _title = WinoStyle.Label(string.Empty, WinoStyle.CaptionStrong, WinoStyle.PrimaryText);
        AddSubview(_strip);
        _strip.AddSubview(_title);
        NSLayoutConstraint.ActivateConstraints(
        [
            _strip.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, (nfloat)WinoMailRowMetrics.ContentLeading),
            _strip.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -(nfloat)WinoMailRowMetrics.ContentTrailing),
            _strip.TopAnchor.ConstraintEqualTo(TopAnchor),
            _strip.BottomAnchor.ConstraintEqualTo(BottomAnchor, -6),
            _title.LeadingAnchor.ConstraintEqualTo(_strip.LeadingAnchor, 12),
            _title.TrailingAnchor.ConstraintLessThanOrEqualTo(_strip.TrailingAnchor, -12),
            _title.CenterYAnchor.ConstraintEqualTo(_strip.CenterYAnchor)
        ]);
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.StaticTextRole;
    }

    /// <summary>Re-reads the theme colour (accent or backdrop changed).</summary>
    public void RefreshTheme() => _strip.Fill = WinoMailListTheme.GroupHeaderFill();

    public string Title
    {
        get => _title.StringValue;
        set { _title.StringValue = value ?? string.Empty; AccessibilityLabel = value; }
    }
}

/// <summary>Group row container: transparent in the flow, opaque while it floats over the rows beneath.</summary>
public class WinoMailGroupRowView : NSTableRowView
{
    public const string ReuseIdentifier = "WinoMailGroupRow";

    public WinoMailGroupRowView() => Identifier = ReuseIdentifier;

    public override bool Floating
    {
        get => base.Floating;
        set { base.Floating = value; NeedsDisplay = true; }
    }

    public override void DrawBackground(CGRect dirtyRect)
    {
        if (!Floating) return;
        WinoMailListTheme.FloatingGroupBackground.SetFill();
        NSBezierPath.FillRect(new CGRect(0, 0, Bounds.Width, Bounds.Height - 6));
    }

    public override void DrawSelection(CGRect dirtyRect)
    {
    }

    public override void DrawSeparator(CGRect dirtyRect)
    {
    }
}
