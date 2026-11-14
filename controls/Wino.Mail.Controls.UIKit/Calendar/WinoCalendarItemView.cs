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

/// <summary>The "n more events" link of a day whose items did not all fit, with the hidden items.</summary>
public sealed record CalendarOverflowCell(DateOnly Date, List<ICalendarItem> HiddenItems, CGRect Bounds)
{
    public string Text => CalendarItemOverflow.FormatMoreText(HiddenItems.Count);
}

/// <summary>
/// UIKit adapter over the shared Domain <see cref="CalendarSurfaceLayoutCalculator"/> (the port of the Windows timed and
/// month layout calculators): converts its neutral rectangles to <see cref="CGRect"/>. Same API as the AppKit adapter.
/// </summary>
public static class CalendarLayoutCalculator
{
    public const int MonthColumns = CalendarSurfaceLayoutCalculator.MonthColumns;
    public const int MonthRows = CalendarSurfaceLayoutCalculator.MonthRows;

    public static double GetAllDayHeight(int laneCount, bool hasMoreRow) => CalendarSurfaceLayoutCalculator.GetAllDayHeight(laneCount, hasMoreRow);

    public static (int Lanes, bool HasOverflow) GetAllDayLaneCount(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items)
        => CalendarSurfaceLayoutCalculator.GetAllDayLaneCount(dates, items);

    /// <summary>Timed and multi-day placements for the hour grid. X is relative to the first day column.</summary>
    public static List<CalendarItemPlacement> CalculateTimed(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items,
        double dayWidth, double hourHeight, CalendarEventDisplayMode mode)
        => CalendarSurfaceLayoutCalculator.CalculateTimed(dates, items, dayWidth, hourHeight, mode).ConvertAll(ToUIKit);

    /// <summary>All-day lanes above the hour grid, and the days whose extra items sit behind an "n more" link.</summary>
    public static (List<CalendarItemPlacement> Items, List<CalendarOverflowCell> Overflows) CalculateAllDay(IReadOnlyList<DateOnly> dates, IEnumerable<ICalendarItem> items, double dayWidth)
    {
        var (placements, overflows) = CalendarSurfaceLayoutCalculator.CalculateAllDay(dates, items, dayWidth);
        return (placements.ConvertAll(ToUIKit), overflows.ConvertAll(ToUIKit));
    }

    /// <summary>The month grid (7 cells per week row). Dates must already be week-aligned.</summary>
    public static (List<CalendarMonthCell> Cells, List<CalendarItemPlacement> Items, List<CalendarOverflowCell> Overflows) CalculateMonth(IReadOnlyList<DateOnly> dates,
        IEnumerable<ICalendarItem> items, double width, double height)
    {
        var (cells, placements, overflows) = CalendarSurfaceLayoutCalculator.CalculateMonth(dates, items, width, height);
        return (cells.ConvertAll(cell => new CalendarMonthCell(cell.Date, cell.Index, cell.Row, cell.Column, ToRect(cell.Bounds))),
            placements.ConvertAll(ToUIKit), overflows.ConvertAll(ToUIKit));
    }

    /// <summary>Expands a month range to full weeks (42 days) like the Windows control.</summary>
    public static IReadOnlyList<DateOnly> MonthGridDates(VisibleDateRange range, DayOfWeek firstDayOfWeek)
        => CalendarSurfaceLayoutCalculator.MonthGridDates(range.StartDate, firstDayOfWeek);

    private static CalendarItemPlacement ToUIKit(CalendarLayoutPlacement placement)
        => new(placement.Item, placement.Date, ToRect(placement.Bounds), placement.Kind);

    private static CalendarOverflowCell ToUIKit(CalendarLayoutOverflowCell overflow)
        => new(overflow.Date, overflow.HiddenItems, ToRect(overflow.Bounds));

    private static CGRect ToRect(CalendarLayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
}

/// <summary>
/// The calendar event tile on UIKit (iOS calendar refresh of 14 Nov 2026, owner-approved departure from the Windows
/// show-as stripe): the calendar colour as a tint over the surface with a 3pt stripe in the calendar colour (dashed for
/// Tentative, purple for Out of office), a label-coloured semibold title, time and location lines, and repeat and
/// multi-day glyphs in the top right. The surface reuses these views between layouts; the
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
        WinoIcons.StyleWatchers.Add(this, static view => view.StyleChanged(null, EventArgs.Empty));
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

    /// <summary>The all-day and month chip title size; the surface sets 12pt in Day, where chips have room.</summary>
    internal double ChipTitleSize { get; set; } = 11;

    private const double NarrowTitleSize = 10.5;
    private const double NarrowLineHeight = 12.5;
    private const double TwoLineTitleMinHeight = 44;
    private static readonly UIColor OutOfOfficeStripe = WinoStyle.Hex(0x9C27B0);

    /// <summary>
    /// The calendar colour's tint over the opaque surface (iOS calendar refresh of 14 Nov 2026): Free and Tentative are
    /// lighter, a pending change (busy) fades the tint, hover strengthens it.
    /// </summary>
    private double TintOpacity(bool dark)
    {
        if (_model is null) return 0;
        bool light = _model.ShowAs is CalendarItemShowAs.Free or CalendarItemShowAs.Tentative;
        double tint = light ? (dark ? 0.18 : 0.10) : (dark ? 0.33 : 0.18);
        if (_model.IsBusy) tint *= 0.6;
        return _hover ? tint + 0.06 : tint;
    }

    public override void Draw(CGRect rect)
    {
        if (_model is null) return;
        var bounds = Bounds;
        var traits = TraitCollection;
        bool dark = traits.UserInterfaceStyle == UIUserInterfaceStyle.Dark;
        var calendar = _model.Fill.GetResolvedColor(traits);
        var surface = WinoStyle.ZoneFill.GetResolvedColor(traits);

        if (_kind == CalendarPlacementKind.MultiDayGhost)
        {
            calendar.ColorWithAlpha((nfloat)(dark ? 0.16 : 0.10)).SetFill();
            UIBezierPath.FromRect(bounds).Fill();
            return;
        }

        bool compact = WinoCalendarTileAppearance.IsSingleLine(_kind);
        bool narrow = bounds.Width < NarrowTileWidth && !compact;
        bool selected = _model.IsSelected;
        nfloat radius = narrow ? 5 : 6;
        var path = UIBezierPath.FromRoundedRect(bounds, radius);
        surface.SetFill();
        path.Fill();
        (selected ? calendar : calendar.ColorWithAlpha((nfloat)TintOpacity(dark))).SetFill();
        path.Fill();

        var context = UIGraphics.GetCurrentContext();
        if (!selected)
        {
            double stripeWidth = narrow ? 2.5 : WinoCalendarTileAppearance.StripeWidth;
            context.SaveState();
            path.AddClip();
            (_model.ShowAs == CalendarItemShowAs.OutOfOffice ? OutOfOfficeStripe : calendar).SetFill();
            if (_model.ShowAs == CalendarItemShowAs.Tentative)
            {
                for (double top = 0; top < bounds.Height; top += 7)
                    UIGraphics.RectFill(new CGRect(0, top, stripeWidth, Math.Min(4, bounds.Height - top)));
            }
            else UIGraphics.RectFill(new CGRect(0, 0, stripeWidth, bounds.Height));
            context.RestoreState();
        }

        // A hairline in the surface colour separates cascaded and adjacent tiles.
        var outline = UIBezierPath.FromRoundedRect(bounds.Inset(0.5f, 0.5f), radius);
        outline.LineWidth = 1;
        surface.SetStroke();
        outline.Stroke();

        var titleColor = selected ? ReadableTextColor(calendar, traits) : WinoStyle.PrimaryText;
        var detailColor = selected ? titleColor.ColorWithAlpha(0.85f) : WinoStyle.SecondaryText;
        if (narrow)
        {
            DrawNarrowTitle(bounds, titleColor);
            return;
        }

        double glyphSize = WinoCalendarTileAppearance.GlyphSize;
        double textLeft = WinoCalendarTileAppearance.StripeWidth + 5;
        double textRight = bounds.Width - 4 - WinoCalendarTileAppearance.GlyphColumnWidth(_model.IsRecurring, _model.IsMultiDay);
        double textWidth = Math.Max(0, textRight - textLeft);
        var paragraph = new NSMutableParagraphStyle { LineBreakMode = UILineBreakMode.TailTruncation, Alignment = UITextAlignment.Left };

        double titleSize = compact ? ChipTitleSize : 13;
        var titleFont = WinoStyle.Fixed(titleSize, UIFontWeight.Semibold);
        int titleLines = !compact && bounds.Height >= TwoLineTitleMinHeight ? 2 : 1;
        double titleHeight = titleLines * titleFont.LineHeight;
        if (titleLines > 1) paragraph.LineBreakMode = UILineBreakMode.WordWrap;
        var titleAttributes = new UIStringAttributes { Font = titleFont, ForegroundColor = titleColor, ParagraphStyle = paragraph };
        double y = compact ? (bounds.Height - titleFont.LineHeight) / 2 : 3;
        if (titleLines > 1)
        {
            // Two lines when the title needs them; the second ends in an ellipsis.
            var measured = new NSAttributedString(_model.Title, titleAttributes).GetBoundingRect(new CGSize(textWidth, titleHeight),
                NSStringDrawingOptions.UsesLineFragmentOrigin | NSStringDrawingOptions.TruncatesLastVisibleLine, null);
            titleHeight = Math.Min(titleHeight, Math.Ceiling((double)measured.Height));
            new NSAttributedString(_model.Title, titleAttributes).DrawString(new CGRect(textLeft, y, textWidth, titleHeight),
                NSStringDrawingOptions.UsesLineFragmentOrigin | NSStringDrawingOptions.TruncatesLastVisibleLine, null);
        }
        else if (bounds.Height >= titleFont.LineHeight - 2)
        {
            new NSAttributedString(_model.Title, titleAttributes).DrawString(new CGRect(textLeft, y, textWidth, titleFont.LineHeight + 1));
        }
        y += titleHeight + 1;

        if (!compact && bounds.Height >= WinoCalendarTileAppearance.DetailLinesMinHeight)
        {
            var detailFont = WinoStyle.Fixed(11);
            var detailAttributes = new UIStringAttributes { Font = detailFont, ForegroundColor = detailColor, ParagraphStyle = new NSMutableParagraphStyle { LineBreakMode = UILineBreakMode.TailTruncation } };
            double detailHeight = detailFont.LineHeight + 1;
            foreach (var line in new[] { _model.TimeRange, _model.Location })
            {
                if (string.IsNullOrWhiteSpace(line) || y + detailHeight > bounds.Height) continue;
                new NSAttributedString(line, detailAttributes).DrawString(new CGRect(textLeft, y, textWidth, detailHeight));
                y += detailHeight + 1;
            }
        }

        double glyphY = compact ? (bounds.Height - glyphSize) / 2 : 4;
        double glyphX = bounds.Width - 4 - glyphSize;
        if (_model.IsMultiDay)
        {
            WinoIcons.Draw(WinoIcons.Glyph(WinoIconGlyph.CalendarEventMuiltiDay), new CGRect(glyphX, glyphY, glyphSize, glyphSize), detailColor, traits, false);
            glyphX -= glyphSize + WinoCalendarTileAppearance.GlyphSpacing;
        }
        if (_model.IsRecurring)
            WinoIcons.Draw(WinoIcons.Glyph(WinoIconGlyph.CalendarEventRepeat), new CGRect(glyphX, glyphY, glyphSize, glyphSize), detailColor, traits, false);
    }

    /// <summary>
    /// Narrow week columns and cascaded tiles: 10.5pt titles that wrap by word (a word wider than the tile breaks by
    /// character) and are clipped by the tile, with no ellipsis and no detail lines, so "Stand-up" never reads "Sta…".
    /// </summary>
    private void DrawNarrowTitle(CGRect bounds, UIColor color)
    {
        if (_model is null) return;
        double left = 2.5 + 2.5, top = 2, right = 1;
        double width = Math.Max(0, bounds.Width - left - right);
        double height = Math.Max(0, bounds.Height - top - 1);
        if (width <= 0 || height <= 0) return;
        var paragraph = new NSMutableParagraphStyle
        {
            LineBreakMode = UILineBreakMode.WordWrap,
            MinimumLineHeight = (nfloat)NarrowLineHeight,
            MaximumLineHeight = (nfloat)NarrowLineHeight
        };
        var attributes = new UIStringAttributes { Font = WinoStyle.Fixed(NarrowTitleSize, UIFontWeight.Semibold), ForegroundColor = color, ParagraphStyle = paragraph };
        var context = UIGraphics.GetCurrentContext();
        context.SaveState();
        context.ClipToRect(new CGRect(left, top, width, height));
        int lines = Math.Max(1, (int)Math.Ceiling(height / NarrowLineHeight));
        new NSAttributedString(_model.Title, attributes).DrawString(new CGRect(left, top, width, lines * NarrowLineHeight), NSStringDrawingOptions.UsesLineFragmentOrigin, null);
        context.RestoreState();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoIcons.StyleWatchers.Remove(this);
        base.Dispose(disposing);
    }
}
