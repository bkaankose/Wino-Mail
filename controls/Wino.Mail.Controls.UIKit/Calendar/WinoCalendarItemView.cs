using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Presentation.Controls;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Calendar;

/// <summary>What one event tile shows (UIKit twin of the AppKit <c>CalendarTileModel</c>). The page builds it from the shared CalendarItemViewModel.</summary>
public sealed record CalendarTileModel(
    string Title,
    string? TimeRange,
    string? Location,
    UIColor Fill,
    CalendarItemShowAs ShowAs,
    bool IsRecurring,
    bool IsMultiDay,
    bool IsSelected,
    bool IsBusy);

/// <summary>One item rectangle on a calendar surface, in the canvas's top-down coordinates.</summary>
public sealed record CalendarItemPlacement(ICalendarItem Item, DateOnly Date, CGRect Bounds, CalendarPlacementKind Kind);

public sealed record CalendarMonthCell(DateOnly Date, int Index, int Row, int Column, CGRect Bounds);

/// <summary>
/// UIKit adapter over the shared Domain <see cref="CalendarSurfaceLayoutCalculator"/> (the port of the Windows timed and
/// month layout calculators): converts its neutral rectangles to <see cref="CGRect"/>. Same API as the AppKit adapter.
/// </summary>
public static class CalendarLayoutCalculator
{
    public const int MonthColumns = CalendarSurfaceLayoutCalculator.MonthColumns;
    public const int MonthRows = CalendarSurfaceLayoutCalculator.MonthRows;

    public static double GetAllDayHeight(int laneCount) => CalendarSurfaceLayoutCalculator.GetAllDayHeight(laneCount);

    public static int GetAllDayLaneCount(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items)
        => CalendarSurfaceLayoutCalculator.GetAllDayLaneCount(dates, items);

    /// <summary>Timed and multi-day placements for the hour grid. X is relative to the first day column.</summary>
    public static List<CalendarItemPlacement> CalculateTimed(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items,
        double dayWidth, double hourHeight, CalendarEventDisplayMode mode)
        => CalendarSurfaceLayoutCalculator.CalculateTimed(dates, items, dayWidth, hourHeight, mode).ConvertAll(ToUIKit);

    /// <summary>All-day lanes above the hour grid.</summary>
    public static List<CalendarItemPlacement> CalculateAllDay(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items, double dayWidth)
        => CalendarSurfaceLayoutCalculator.CalculateAllDay(dates, items, dayWidth).ConvertAll(ToUIKit);

    /// <summary>The 6x7 month grid. Dates must already be week-aligned (42 entries).</summary>
    public static (List<CalendarMonthCell> Cells, List<CalendarItemPlacement> Items) CalculateMonth(IReadOnlyList<DateOnly> dates,
        IEnumerable<ICalendarItem> items, double width, double height)
    {
        var (cells, placements) = CalendarSurfaceLayoutCalculator.CalculateMonth(dates, items, width, height);
        return (cells.ConvertAll(cell => new CalendarMonthCell(cell.Date, cell.Index, cell.Row, cell.Column, ToRect(cell.Bounds))), placements.ConvertAll(ToUIKit));
    }

    /// <summary>Expands a month range to full weeks (42 days) like the Windows control.</summary>
    public static IReadOnlyList<DateOnly> MonthGridDates(VisibleDateRange range, DayOfWeek firstDayOfWeek)
        => CalendarSurfaceLayoutCalculator.MonthGridDates(range.StartDate, firstDayOfWeek);

    private static CalendarItemPlacement ToUIKit(CalendarLayoutPlacement placement)
        => new(placement.Item, placement.Date, ToRect(placement.Bounds), placement.Kind);

    private static CGRect ToRect(CalendarLayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}

/// <summary>
/// The Windows CalendarItemControl on UIKit (twin of the AppKit <c>WinoCalendarItemView</c>): a 3pt "show as" stripe,
/// the calendar colour at 0.9 opacity, a semibold title, time and location lines, and repeat and multi-day glyphs in the
/// top right (shared <see cref="WinoCalendarTileAppearance"/>). The surface reuses these views between layouts; the
/// surface's gestures handle taps, long presses and drags.
/// </summary>
public sealed class WinoCalendarItemView : UIView
{
    /// <summary>Below this width a tile drops its glyphs and detail lines for the title.</summary>
    public const double NarrowTileWidth = 64;

    private CalendarTileModel? _model;
    private CalendarPlacementKind _kind;
    private bool _hover;

    public WinoCalendarItemView()
    {
        Opaque = false;
        BackgroundColor = UIColor.Clear;
        ContentMode = UIViewContentMode.Redraw;
        IsAccessibilityElement = true;
        AccessibilityTraits = UIAccessibilityTrait.Button;
        WinoTraits.OnAppearanceChanged(this, static view => view.SetNeedsDisplay());
        WinoIcons.StyleChanged += StyleChanged;
        AddGestureRecognizer(new UIHoverGestureRecognizer(recognizer =>
        {
            _hover = recognizer.State is UIGestureRecognizerState.Began or UIGestureRecognizerState.Changed;
            SetNeedsDisplay();
        }));
    }

    /// <summary>The item this tile stands for (page-defined).</summary>
    public object? Item { get; set; }

    public CalendarTileModel? Model
    {
        get => _model;
        set
        {
            _model = value;
            AccessibilityLabel = value is null ? null : string.Join(", ", new[] { value.Title, value.TimeRange, value.Location }.Where(part => !string.IsNullOrWhiteSpace(part)));
            AccessibilityTraits = value?.IsSelected == true ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
            SetNeedsDisplay();
        }
    }

    public CalendarPlacementKind Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            IsAccessibilityElement = value != CalendarPlacementKind.MultiDayGhost;
            UserInteractionEnabled = value != CalendarPlacementKind.MultiDayGhost;
            SetNeedsDisplay();
        }
    }

    /// <summary>The day this tile is placed on (set by the hosting surface).</summary>
    internal DateOnly Date { get; set; }

    /// <summary>Whether the bottom edge starts a resize drag (timed, single-day items the page lets move).</summary>
    public bool AllowsResize { get; set; }

    private void StyleChanged(object? sender, EventArgs args) => SetNeedsDisplay();

    /// <summary>Stripe colours from the Windows ShowAs stripe templates.</summary>
    public static UIColor StripeColor(CalendarItemShowAs showAs) => WinoStyle.Hex(WinoCalendarTileAppearance.StripeColor(showAs));

    /// <summary>Black or white, whichever reads on the fill (the shared perceived-brightness rule).</summary>
    public static UIColor ReadableTextColor(UIColor fill, UITraitCollection? traits = null) => WinoStyle.ReadableText(fill, traits);

    public override void Draw(CGRect rect)
    {
        if (_model is null) return;
        var bounds = Bounds;
        bool ghost = _kind == CalendarPlacementKind.MultiDayGhost;
        bool compact = WinoCalendarTileAppearance.IsSingleLine(_kind);
        double opacity = WinoCalendarTileAppearance.FillOpacity(_kind, _model.IsBusy, _model.IsMultiDay, _hover);
        nfloat radius = (nfloat)(ghost ? 0 : WinoCalendarTileAppearance.CornerRadius);

        var path = UIBezierPath.FromRoundedRect(bounds, radius);
        var fill = _model.Fill.GetResolvedColor(TraitCollection);
        fill.ColorWithAlpha((nfloat)opacity).SetFill();
        path.Fill();
        if (ghost) return;

        // Show-as stripe, clipped to the rounded shape.
        var context = UIGraphics.GetCurrentContext();
        context.SaveState();
        path.AddClip();
        StripeColor(_model.ShowAs).SetFill();
        UIGraphics.RectFill(new CGRect(0, 0, WinoCalendarTileAppearance.StripeWidth, bounds.Height));
        context.RestoreState();

        if (_model.IsSelected)
        {
            var inner = UIBezierPath.FromRoundedRect(bounds.Inset(1, 1), radius);
            inner.LineWidth = 1.5f;
            WinoStyle.Accent.SetStroke();
            inner.Stroke();
        }

        var textColor = ReadableTextColor(fill, TraitCollection);
        var paragraph = new NSMutableParagraphStyle { LineBreakMode = UILineBreakMode.TailTruncation, Alignment = UITextAlignment.Left };
        double glyphSize = WinoCalendarTileAppearance.GlyphSize;
        // Narrow tiles (week columns on iPhone and iPad portrait, overlapping events) give the title every point: tighter
        // insets, no glyph column, a smaller font and, in a tall tile, the title wrapped over several lines instead of the
        // time and location, so it never shrinks to one letter.
        bool narrow = bounds.Width < NarrowTileWidth;
        double textLeft = WinoCalendarTileAppearance.StripeWidth + (narrow ? 3 : 6);
        double textRight = bounds.Width - (narrow ? 2 : 4) - (narrow ? 0 : WinoCalendarTileAppearance.GlyphColumnWidth(_model.IsRecurring, _model.IsMultiDay));
        double textWidth = Math.Max(0, textRight - textLeft);

        double titleSize = WinoCalendarTileAppearance.TitleFontSize(_kind, _model.IsMultiDay, bounds.Height);
        var titleFont = WinoStyle.Fixed(narrow ? Math.Min(titleSize, 11) : titleSize, UIFontWeight.Semibold);
        double titleHeight = titleFont.LineHeight + 2;
        int titleLines = narrow && !compact ? Math.Clamp((int)Math.Floor((bounds.Height - 3) / titleFont.LineHeight), 1, 4) : 1;
        if (titleLines > 1)
        {
            paragraph.LineBreakMode = UILineBreakMode.WordWrap;
            titleHeight = titleLines * titleFont.LineHeight;
        }
        var titleAttributes = new UIStringAttributes { Font = titleFont, ForegroundColor = textColor, ParagraphStyle = paragraph };
        double y = compact ? (bounds.Height - titleHeight) / 2 : 3;
        if (bounds.Height >= titleFont.LineHeight)
            new NSAttributedString(_model.Title, titleAttributes).DrawString(new CGRect(textLeft, y, textWidth, titleHeight));
        y += titleHeight + 2;
        if (narrow) return;

        if (!compact && bounds.Height >= WinoCalendarTileAppearance.DetailLinesMinHeight)
        {
            var detailFont = WinoStyle.Fixed(11);
            var detailAttributes = new UIStringAttributes { Font = detailFont, ForegroundColor = textColor.ColorWithAlpha(0.92f), ParagraphStyle = paragraph };
            double detailHeight = detailFont.LineHeight + 2;
            foreach (var line in new[] { _model.TimeRange, _model.Location })
            {
                if (string.IsNullOrWhiteSpace(line) || y + detailHeight > bounds.Height) continue;
                new NSAttributedString(line, detailAttributes).DrawString(new CGRect(textLeft, y, textWidth, detailHeight));
                y += detailHeight + 2;
            }
        }

        double glyphY = compact ? (bounds.Height - glyphSize) / 2 : 4;
        double glyphX = bounds.Width - 4 - glyphSize;
        if (_model.IsMultiDay)
        {
            WinoIcons.Draw(WinoIcons.Glyph(WinoIconGlyph.CalendarEventMuiltiDay), new CGRect(glyphX, glyphY, glyphSize, glyphSize), textColor, TraitCollection, false);
            glyphX -= glyphSize + WinoCalendarTileAppearance.GlyphSpacing;
        }
        if (_model.IsRecurring)
            WinoIcons.Draw(WinoIcons.Glyph(WinoIconGlyph.CalendarEventRepeat), new CGRect(glyphX, glyphY, glyphSize, glyphSize), textColor, TraitCollection, false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoIcons.StyleChanged -= StyleChanged;
        base.Dispose(disposing);
    }
}
