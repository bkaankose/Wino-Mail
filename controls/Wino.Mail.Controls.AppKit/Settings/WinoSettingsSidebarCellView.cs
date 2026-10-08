using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Settings;

/// <summary>
/// Settings sidebar page row in the Windows settings pane style: a 15pt Wino glyph (or a custom
/// image such as the app icon) and the page title, semibold while selected. The selection fill and
/// the 3pt accent pipe are drawn by <see cref="WinoSettingsSidebarRowView"/>.
/// </summary>
public sealed class WinoSettingsSidebarCellView : NSTableCellView
{
    public const string RowIdentifier = "WinoSettingsSidebarRow";
    public const string HeaderIdentifier = "WinoSettingsSidebarHeader";
    public const double RowHeight = 34;
    public const double HeaderHeight = 40;
    public const double FirstHeaderHeight = 30;

    private readonly WinoIconView _glyph;
    private readonly NSImageView _image;
    private readonly NSTextField _title;

    public WinoSettingsSidebarCellView()
    {
        Identifier = RowIdentifier;
        _glyph = new WinoIconView(WinoIconGlyph.None, 15);
        WinoLayout.Size(_glyph, 16, 16);
        _image = new NSImageView { ImageScaling = NSImageScale.ProportionallyDown, TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        WinoLayout.Size(_image, 16, 16);
        _title = WinoStyle.Label(string.Empty, WinoStyle.Body, WinoStyle.PrimaryText);
        _title.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        TextField = _title;
        var row = WinoLayout.HStack(10, _glyph, _image, _title);
        row.TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(row);
        NSLayoutConstraint.ActivateConstraints(
        [
            // The accent pipe ends at x = 6; the glyph sits 8pt beside it and centres under the header circles.
            row.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 14),
            row.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -18),
            row.CenterYAnchor.ConstraintEqualTo(CenterYAnchor)
        ]);
    }

    /// <summary>Shows a Wino glyph, optionally tinted (Wino Intelligence uses its brand colour).</summary>
    public void Configure(string title, WinoIconGlyph glyph, NSColor? tint = null)
    {
        _title.StringValue = title;
        _glyph.Icon = glyph;
        _glyph.Tint = tint;
        _glyph.Hidden = false;
        _image.Hidden = true;
        AccessibilityLabel = title;
    }

    /// <summary>Shows a bitmap instead of a glyph (the Wino account row uses the app icon).</summary>
    public void Configure(string title, NSImage image)
    {
        _title.StringValue = title;
        _image.Image = image;
        _image.Hidden = false;
        _glyph.Hidden = true;
        AccessibilityLabel = title;
    }

    public override NSBackgroundStyle BackgroundStyle
    {
        get => base.BackgroundStyle;
        set
        {
            base.BackgroundStyle = value;
            var selected = (Superview as NSTableRowView)?.Selected ?? false;
            _title.Font = selected ? WinoStyle.BodyStrong : WinoStyle.Body;
        }
    }

    /// <summary>
    /// A group header: a 24pt circle with a 12pt glyph, the semibold caps title and a divider line
    /// that runs to the trailing edge (Windows SettingsShellSectionItemTemplate).
    /// </summary>
    public static NSTableCellView CreateHeader(string title, WinoIconGlyph glyph)
    {
        var cell = new NSTableCellView { Identifier = HeaderIdentifier };
        var circle = new WinoSurfaceView { CornerRadius = 12, Fill = WinoSettingsStyle.CardFill, Stroke = WinoSettingsStyle.CardStroke };
        WinoLayout.Size(circle, 24, 24);
        var icon = new WinoIconView(glyph, 12, WinoStyle.SecondaryText);
        circle.AddSubview(icon);
        NSLayoutConstraint.ActivateConstraints(
        [
            icon.CenterXAnchor.ConstraintEqualTo(circle.CenterXAnchor),
            icon.CenterYAnchor.ConstraintEqualTo(circle.CenterYAnchor)
        ]);
        var label = WinoStyle.Label(string.Empty, WinoStyle.CaptionStrong, WinoStyle.SecondaryText);
        label.AttributedStringValue = new NSAttributedString((title ?? string.Empty).ToUpperInvariant(), new NSStringAttributes
        {
            Font = WinoStyle.CaptionStrong,
            ForegroundColor = WinoStyle.SecondaryText,
            KerningAdjustment = 0.6f
        });
        cell.TextField = label;
        var line = new WinoSurfaceView { Fill = WinoSettingsStyle.CardStroke };
        line.HeightAnchor.ConstraintEqualTo(1).Active = true;
        line.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Horizontal);
        var row = WinoLayout.HStack(8, circle, label, line);
        row.Distribution = NSStackViewDistribution.Fill;
        cell.AddSubview(row);
        NSLayoutConstraint.ActivateConstraints(
        [
            row.LeadingAnchor.ConstraintEqualTo(cell.LeadingAnchor, 10),
            row.TrailingAnchor.ConstraintEqualTo(cell.TrailingAnchor, -10),
            row.BottomAnchor.ConstraintEqualTo(cell.BottomAnchor, -4),
            row.HeightAnchor.ConstraintEqualTo(24)
        ]);
        return cell;
    }
}

/// <summary>
/// Row background for the settings sidebar: a subtle rounded fill (radius 5, 8pt side margins)
/// with a 3pt accent pipe on the leading edge while selected. Headers draw nothing.
/// </summary>
public sealed class WinoSettingsSidebarRowView : NSTableRowView
{
    public override void DrawSelection(CGRect dirtyRect)
    {
        if (!Selected || SelectionHighlightStyle == NSTableViewSelectionHighlightStyle.None && !Selected) return;
        var bounds = Bounds;
        var fill = new CGRect(bounds.X + 8, bounds.Y + 1, bounds.Width - 16, bounds.Height - 2);
        WinoSettingsStyle.SelectedFill.SetFill();
        NSBezierPath.FromRoundedRect(fill, 5, 5).Fill();
        var pipe = new CGRect(bounds.X + 3, bounds.Y + 9, 3, bounds.Height - 18);
        WinoStyle.Accent.SetFill();
        NSBezierPath.FromRoundedRect(pipe, 1.5f, 1.5f).Fill();
    }

    public override void DrawBackground(CGRect dirtyRect)
    {
        // Transparent over the theme backdrop; the selection pass paints the highlight.
    }

    public override NSBackgroundStyle InteriorBackgroundStyle => NSBackgroundStyle.Normal;
}
