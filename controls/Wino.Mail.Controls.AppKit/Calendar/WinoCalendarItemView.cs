using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Calendar;

/// <summary>What one event tile shows. The page builds it from the shared CalendarItemViewModel.</summary>
public sealed record CalendarTileModel(
    string Title,
    string? TimeRange,
    string? Location,
    NSColor Fill,
    CalendarItemShowAs ShowAs,
    bool IsRecurring,
    bool IsMultiDay,
    bool IsSelected,
    bool IsBusy);

/// <summary>
/// The Windows CalendarItemControl: a 3pt "show as" stripe, the calendar colour at 0.9 opacity,
/// a 13pt semibold title, 11pt time and location lines, and repeat/multi-day glyphs in the top
/// right. The surface reuses these views between layouts and draws everything in one pass.
/// </summary>
public sealed class WinoCalendarItemView : NSView
{
    private const double StripeWidth = 3;
    private const double CornerRadius = 2;
    private CalendarTileModel? _model;
    private CalendarPlacementKind _kind;
    private bool _hover;

    public WinoCalendarItemView()
    {
        WantsLayer = true;
        WinoIcons.StyleChanged += StyleChanged;
    }

    /// <summary>The item this tile stands for (page-defined).</summary>
    public object? Item { get; set; }

    public CalendarTileModel? Model
    {
        get => _model;
        set { _model = value; ToolTip = value?.Title; NeedsDisplay = true; }
    }

    public CalendarPlacementKind Kind
    {
        get => _kind;
        set { _kind = value; NeedsDisplay = true; }
    }

    public event EventHandler<NSEvent>? Clicked;
    public event EventHandler<NSEvent>? DoubleClicked;
    public event EventHandler<NSEvent>? RightClicked;

    public override bool IsFlipped => true;
    public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

    public override NSView? HitTest(CGPoint point) => _kind == CalendarPlacementKind.MultiDayGhost ? null : base.HitTest(point);

    public override void MouseDown(NSEvent theEvent)
    {
        if (theEvent.ClickCount == 2) DoubleClicked?.Invoke(this, theEvent);
        else if (theEvent.ClickCount == 1) Clicked?.Invoke(this, theEvent);
    }

    public override void RightMouseDown(NSEvent theEvent) => RightClicked?.Invoke(this, theEvent);

    public override void UpdateTrackingAreas()
    {
        base.UpdateTrackingAreas();
        foreach (var area in TrackingAreas()) RemoveTrackingArea(area);
        AddTrackingArea(new NSTrackingArea(Bounds, NSTrackingAreaOptions.MouseEnteredAndExited | NSTrackingAreaOptions.ActiveInKeyWindow | NSTrackingAreaOptions.InVisibleRect, this, null));
    }

    public override void MouseEntered(NSEvent theEvent) { _hover = true; NeedsDisplay = true; }
    public override void MouseExited(NSEvent theEvent) { _hover = false; NeedsDisplay = true; }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        NeedsDisplay = true;
    }

    private void StyleChanged(object? sender, EventArgs args) => NeedsDisplay = true;

    /// <summary>Stripe colours from the Windows ShowAs stripe templates.</summary>
    public static NSColor StripeColor(CalendarItemShowAs showAs) => showAs switch
    {
        CalendarItemShowAs.Free => WinoStyle.Hex(0x4CAF50),
        CalendarItemShowAs.Tentative => WinoStyle.Hex(0xFFC107),
        CalendarItemShowAs.OutOfOffice => WinoStyle.Hex(0x9C27B0),
        CalendarItemShowAs.WorkingElsewhere => WinoStyle.Hex(0x2196F3),
        _ => WinoStyle.Hex(0xF44336)
    };

    /// <summary>Black or white, whichever reads on the fill (Windows XamlHelpers.GetReadableTextColor).</summary>
    public static NSColor ReadableTextColor(NSColor fill)
    {
        var rgb = fill.UsingColorSpace(NSColorSpace.SRGBColorSpace) ?? fill;
        double luminance = 0.299 * rgb.RedComponent + 0.587 * rgb.GreenComponent + 0.114 * rgb.BlueComponent;
        return luminance > 0.6 ? NSColor.Black : NSColor.White;
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        if (_model is null) return;
        var bounds = Bounds;
        bool ghost = _kind == CalendarPlacementKind.MultiDayGhost;
        bool compact = _kind is CalendarPlacementKind.AllDay or CalendarPlacementKind.MonthCell;
        double opacity = ghost ? 0.2 : _model.IsBusy ? 0.55 : _kind == CalendarPlacementKind.MonthCell && _model.IsMultiDay ? 1 : 0.9;
        if (_hover && !ghost) opacity = Math.Min(1, opacity + 0.08);

        var path = NSBezierPath.FromRoundedRect(bounds, (nfloat)(ghost ? 0 : CornerRadius), (nfloat)(ghost ? 0 : CornerRadius));
        _model.Fill.ColorWithAlphaComponent((nfloat)opacity).SetFill();
        path.Fill();

        if (ghost) return;

        // Show-as stripe, clipped to the rounded shape.
        NSGraphicsContext.CurrentContext?.SaveGraphicsState();
        path.AddClip();
        StripeColor(_model.ShowAs).SetFill();
        NSBezierPath.FromRect(new CGRect(0, 0, StripeWidth, bounds.Height)).Fill();
        NSGraphicsContext.CurrentContext?.RestoreGraphicsState();

        if (_model.IsSelected)
        {
            var inner = NSBezierPath.FromRoundedRect(bounds.Inset(1, 1), (nfloat)CornerRadius, (nfloat)CornerRadius);
            inner.LineWidth = 1.5f;
            WinoStyle.Accent.SetStroke();
            inner.Stroke();
        }

        var textColor = ReadableTextColor(_model.Fill);
        var paragraph = new NSMutableParagraphStyle { LineBreakMode = NSLineBreakMode.TruncatingTail, Alignment = NSTextAlignment.Left };
        double glyphSize = 12;
        int glyphCount = (_model.IsRecurring ? 1 : 0) + (_model.IsMultiDay ? 1 : 0);
        double glyphsWidth = glyphCount == 0 ? 0 : glyphCount * glyphSize + (glyphCount - 1) * 6 + 4;
        double textLeft = StripeWidth + 6;
        double textRight = bounds.Width - 4 - glyphsWidth;
        double textWidth = Math.Max(0, textRight - textLeft);

        var titleFont = NSFont.SystemFontOfSize(_kind == CalendarPlacementKind.MonthCell && !_model.IsMultiDay && !compact ? 12 : 13, NSFontWeight.Semibold);
        if (_kind == CalendarPlacementKind.MonthCell && bounds.Height < 24) titleFont = NSFont.SystemFontOfSize(11, NSFontWeight.Semibold);
        var titleAttributes = new NSStringAttributes { Font = titleFont, ForegroundColor = textColor, ParagraphStyle = paragraph };
        double titleHeight = titleFont.Ascender - titleFont.Descender + 2;
        double y = compact ? (bounds.Height - titleHeight) / 2 : 3;
        if (bounds.Height >= titleHeight)
            new NSAttributedString(_model.Title, titleAttributes).DrawInRect(new CGRect(textLeft, y, textWidth, titleHeight));
        y += titleHeight + 2;

        if (!compact && bounds.Height >= 52)
        {
            var detailFont = NSFont.SystemFontOfSize(11);
            var detailAttributes = new NSStringAttributes { Font = detailFont, ForegroundColor = textColor.ColorWithAlphaComponent((nfloat)0.92), ParagraphStyle = paragraph };
            double detailHeight = detailFont.Ascender - detailFont.Descender + 2;
            foreach (var line in new[] { _model.TimeRange, _model.Location })
            {
                if (string.IsNullOrWhiteSpace(line) || y + detailHeight > bounds.Height) continue;
                new NSAttributedString(line, detailAttributes).DrawInRect(new CGRect(textLeft, y, textWidth, detailHeight));
                y += detailHeight + 2;
            }
        }

        if (glyphCount > 0)
        {
            double glyphY = compact ? (bounds.Height - glyphSize) / 2 : 4;
            double glyphX = bounds.Width - 4 - glyphSize;
            if (_model.IsMultiDay)
            {
                WinoIcons.Draw(WinoIcons.Glyph(WinoIconGlyph.CalendarEventMuiltiDay), new CGRect(glyphX, glyphY, glyphSize, glyphSize), textColor, EffectiveAppearance, false);
                glyphX -= glyphSize + 6;
            }
            if (_model.IsRecurring)
                WinoIcons.Draw(WinoIcons.Glyph(WinoIconGlyph.CalendarEventRepeat), new CGRect(glyphX, glyphY, glyphSize, glyphSize), textColor, EffectiveAppearance, false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoIcons.StyleChanged -= StyleChanged;
        base.Dispose(disposing);
    }
}
