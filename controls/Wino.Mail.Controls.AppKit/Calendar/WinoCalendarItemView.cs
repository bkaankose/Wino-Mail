using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;
using Wino.Presentation.Controls;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core.Calendar;

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
    bool IsBusy,
    bool IsAllDay = false);

/// <summary>
/// The Mac event tile, drawn like Apple Calendar: a tinted body in the calendar colour, title
/// and details in a darkened shade of the same colour, and a 3pt bar on the left whose pattern
/// encodes "show as" (solid busy, dashed tentative, hollow free, dotted out of office). All-day
/// and multi-day bands stay solid so they read as bands; month cells show a colour dot and the
/// title. The surface reuses these views between layouts and draws everything in one pass.
/// </summary>
public sealed class WinoCalendarItemView : NSView
{
    private const double BarWidth = WinoCalendarTileAppearance.StripeWidth;
    private const double CornerRadius = 5;
    private const double MonthDotSize = 8;
    /// <summary>Height of the bottom-edge strip that resizes a timed tile instead of moving it.</summary>
    public const double ResizeHandleHeight = WinoCalendarSurfaceGeometry.ResizeHandleHeight;
    private const double DragThreshold = WinoCalendarSurfaceGeometry.TileDragThreshold;
    private CalendarTileModel? _model;
    private CalendarPlacementKind _kind;
    private bool _hover;
    private bool _allowsResize;
    private NSEvent? _pressEvent;
    private bool _pressOnResizeHandle;
    private bool _dragging;

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

    /// <summary>The day this tile is placed on (set by the hosting surface).</summary>
    internal DateOnly Date { get; set; }

    /// <summary>Whether the bottom edge shows the resize cursor and starts a resize drag.</summary>
    public bool AllowsResize
    {
        get => _allowsResize;
        set
        {
            if (_allowsResize == value) return;
            _allowsResize = value;
            Window?.InvalidateCursorRectsForView(this);
        }
    }

    public event EventHandler<NSEvent>? Clicked;
    public event EventHandler<NSEvent>? DoubleClicked;
    public event EventHandler<NSEvent>? RightClicked;

    /// <summary>The pointer moved past the drag threshold: the press event and whether it grabbed the resize edge.</summary>
    internal event EventHandler<(NSEvent Press, bool Resize)>? DragBegan;
    internal event EventHandler<NSEvent>? DragMoved;
    internal event EventHandler<NSEvent>? DragEnded;

    public override bool IsFlipped => true;
    public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

    public override NSView? HitTest(CGPoint point) => _kind == CalendarPlacementKind.MultiDayGhost ? null : base.HitTest(point);

    public override void MouseDown(NSEvent theEvent)
    {
        _dragging = false;
        _pressEvent = theEvent.ClickCount == 1 ? theEvent : null;
        _pressOnResizeHandle = _allowsResize && ConvertPointFromView(theEvent.LocationInWindow, null).Y >= Bounds.Height - ResizeHandleHeight;
        if (theEvent.ClickCount == 2) DoubleClicked?.Invoke(this, theEvent);
        else if (theEvent.ClickCount == 1) Clicked?.Invoke(this, theEvent);
    }

    public override void MouseDragged(NSEvent theEvent)
    {
        if (_pressEvent is not { } press) return;
        if (!_dragging)
        {
            var dx = theEvent.LocationInWindow.X - press.LocationInWindow.X;
            var dy = theEvent.LocationInWindow.Y - press.LocationInWindow.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < DragThreshold) return;
            _dragging = true;
            DragBegan?.Invoke(this, (press, _pressOnResizeHandle));
        }
        DragMoved?.Invoke(this, theEvent);
    }

    public override void MouseUp(NSEvent theEvent)
    {
        bool wasDragging = _dragging;
        _dragging = false;
        _pressEvent = null;
        if (wasDragging) DragEnded?.Invoke(this, theEvent);
    }

    public override void ResetCursorRects()
    {
        base.ResetCursorRects();
        if (_allowsResize && Bounds.Height > ResizeHandleHeight * 2)
            AddCursorRect(new CGRect(0, Bounds.Height - ResizeHandleHeight, Bounds.Width, ResizeHandleHeight), NSCursor.ResizeUpDownCursor);
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
    public static NSColor StripeColor(CalendarItemShowAs showAs) => WinoStyle.Hex(WinoCalendarTileAppearance.StripeColor(showAs));

    /// <summary>Black or white, whichever reads on the fill (the shared perceived-brightness rule).</summary>
    public static NSColor ReadableTextColor(NSColor fill)
    {
        var rgb = fill.UsingColorSpace(NSColorSpace.SRGBColorSpace) ?? fill;
        return Wino.Core.Domain.Presentation.ReadableTextColor.PrefersDarkText(rgb.RedComponent, rgb.GreenComponent, rgb.BlueComponent)
            ? NSColor.Black
            : NSColor.White;
    }

    /// <summary>The calendar colour mixed towards the label colour so it reads as text on the tint.</summary>
    private NSColor TextOn(NSColor fill, double share)
    {
        var rgb = fill.UsingColorSpace(NSColorSpace.SRGBColorSpace) ?? fill;
        var label = (WinoIcons.IsDark(EffectiveAppearance) ? NSColor.White : NSColor.Black).UsingColorSpace(NSColorSpace.SRGBColorSpace)!;
        return label.BlendedColor((nfloat)share, rgb) ?? fill;
    }

    private static bool IsBand(CalendarPlacementKind kind, CalendarTileModel model)
        => kind == CalendarPlacementKind.AllDay || (kind == CalendarPlacementKind.MonthCell && (model.IsMultiDay || model.IsAllDay));

    private void DrawBar(CGRect bounds, NSBezierPath clip, NSColor color, CalendarItemShowAs showAs)
    {
        NSGraphicsContext.CurrentContext?.SaveGraphicsState();
        clip.AddClip();
        color.SetFill();
        switch (showAs)
        {
            case CalendarItemShowAs.Free:
                NSBezierPath.FromRect(new CGRect(0, 0, 1.5, bounds.Height)).Fill();
                break;
            case CalendarItemShowAs.Tentative:
                for (double y = 2; y < bounds.Height; y += 7)
                    NSBezierPath.FromRect(new CGRect(0, y, BarWidth, Math.Min(4, bounds.Height - y))).Fill();
                break;
            case CalendarItemShowAs.OutOfOffice:
                for (double y = 2; y < bounds.Height; y += 4)
                    NSBezierPath.FromRect(new CGRect(0, y, BarWidth, Math.Min(2, bounds.Height - y))).Fill();
                break;
            default:
                NSBezierPath.FromRect(new CGRect(0, 0, BarWidth, bounds.Height)).Fill();
                break;
        }
        NSGraphicsContext.CurrentContext?.RestoreGraphicsState();
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        if (_model is null) return;
        var bounds = Bounds;
        bool ghost = _kind == CalendarPlacementKind.MultiDayGhost;
        bool compact = WinoCalendarTileAppearance.IsSingleLine(_kind);
        bool band = IsBand(_kind, _model);
        bool monthEntry = _kind == CalendarPlacementKind.MonthCell && !band;
        bool dark = WinoIcons.IsDark(EffectiveAppearance);
        var fill = _model.Fill;

        var path = NSBezierPath.FromRoundedRect(bounds, (nfloat)(ghost ? 0 : CornerRadius), (nfloat)(ghost ? 0 : CornerRadius));
        if (ghost)
        {
            fill.ColorWithAlphaComponent(0.12f).SetFill();
            path.Fill();
            return;
        }

        NSColor textColor;
        NSColor detailColor;
        if (band)
        {
            double opacity = _model.IsBusy ? 0.55 : _hover ? 1 : 0.92;
            fill.ColorWithAlphaComponent((nfloat)opacity).SetFill();
            path.Fill();
            textColor = ReadableTextColor(fill);
            detailColor = textColor.ColorWithAlphaComponent(0.92f);
        }
        else if (monthEntry)
        {
            if (_hover || _model.IsSelected)
            {
                fill.ColorWithAlphaComponent((nfloat)(_model.IsSelected ? 0.22 : 0.1)).SetFill();
                path.Fill();
            }
            textColor = _model.IsBusy ? WinoStyle.SecondaryText : WinoStyle.PrimaryText;
            detailColor = WinoStyle.SecondaryText;
            fill.SetFill();
            NSBezierPath.FromOvalInRect(new CGRect(4, (bounds.Height - MonthDotSize) / 2, MonthDotSize, MonthDotSize)).Fill();
        }
        else
        {
            double tint = dark ? 0.3 : 0.2;
            if (_model.IsSelected) tint += 0.12;
            if (_hover) tint += 0.06;
            if (_model.IsBusy) tint *= 0.6;
            fill.ColorWithAlphaComponent((nfloat)tint).SetFill();
            path.Fill();
            DrawBar(bounds, path, _model.IsBusy ? fill.ColorWithAlphaComponent(0.5f) : fill, _model.ShowAs);
            textColor = TextOn(fill, dark ? 0.78 : 0.72);
            detailColor = TextOn(fill, dark ? 0.62 : 0.55).ColorWithAlphaComponent(0.92f);
        }

        if (_model.IsSelected)
        {
            var ring = NSBezierPath.FromRoundedRect(bounds.Inset(1, 1), (nfloat)(CornerRadius - 1), (nfloat)(CornerRadius - 1));
            ring.LineWidth = 2;
            (band ? ReadableTextColor(fill).ColorWithAlphaComponent(0.8f) : fill).SetStroke();
            ring.Stroke();
        }

        var paragraph = new NSMutableParagraphStyle { LineBreakMode = NSLineBreakMode.TruncatingTail, Alignment = NSTextAlignment.Left };
        double glyphSize = WinoCalendarTileAppearance.GlyphSize;
        int glyphCount = (_model.IsRecurring ? 1 : 0) + (_model.IsMultiDay ? 1 : 0);
        double glyphsWidth = WinoCalendarTileAppearance.GlyphColumnWidth(_model.IsRecurring, _model.IsMultiDay);
        double textLeft = monthEntry ? 4 + MonthDotSize + 5 : band ? 7 : BarWidth + 7;
        double textRight = bounds.Width - 4 - glyphsWidth;
        double textWidth = Math.Max(0, textRight - textLeft);

        var titleFont = NSFont.SystemFontOfSize((nfloat)WinoCalendarTileAppearance.TitleFontSize(_kind, _model.IsMultiDay, bounds.Height), monthEntry ? NSFontWeight.Medium : NSFontWeight.Semibold);
        var titleAttributes = new NSStringAttributes { Font = titleFont, ForegroundColor = textColor, ParagraphStyle = paragraph };
        double titleHeight = titleFont.Ascender - titleFont.Descender + 2;
        double y = compact ? (bounds.Height - titleHeight) / 2 : 3;
        if (bounds.Height >= titleHeight)
        {
            if (monthEntry && StartTime(_model.TimeRange) is { } start)
            {
                // Month entries lead with the start time in the secondary colour, as Apple Calendar does.
                var timeAttributes = new NSStringAttributes { Font = NSFont.MonospacedDigitSystemFontOfSize(titleFont.PointSize, NSFontWeight.Regular), ForegroundColor = detailColor, ParagraphStyle = paragraph };
                var time = new NSAttributedString(start, timeAttributes);
                double timeWidth = Math.Ceiling(time.Size.Width) + 4;
                if (timeWidth < textWidth * 0.5)
                {
                    time.DrawInRect(new CGRect(textLeft, y, timeWidth, titleHeight));
                    textLeft += timeWidth;
                    textWidth -= timeWidth;
                }
            }
            new NSAttributedString(_model.Title, titleAttributes).DrawInRect(new CGRect(textLeft, y, textWidth, titleHeight));
        }
        y += titleHeight + 2;

        if (!compact && bounds.Height >= WinoCalendarTileAppearance.DetailLinesMinHeight)
        {
            var detailFont = NSFont.SystemFontOfSize(11);
            var detailAttributes = new NSStringAttributes { Font = detailFont, ForegroundColor = detailColor, ParagraphStyle = paragraph };
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

    /// <summary>The start half of a "start – end" time range.</summary>
    private static string? StartTime(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;
        var separator = range.IndexOf(" – ", StringComparison.Ordinal);
        return separator > 0 ? range[..separator] : null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoIcons.StyleChanged -= StyleChanged;
        base.Dispose(disposing);
    }
}
