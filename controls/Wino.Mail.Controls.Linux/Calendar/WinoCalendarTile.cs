using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Presentation;
using Wino.Presentation.Controls;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>
/// One event tile, self-drawn like the macOS tile: a tinted body in the calendar colour, title and details mixed from
/// that colour, and a 3 px "show as" bar (solid busy, dashed tentative, thin free, dotted out of office). All-day and
/// multi-day bands stay solid; month cells show a dot and the title. Windows adds three states: cancelled (outline,
/// struck title), needs response (dashed outline) and past (lighter). Text comes from the surface's layout cache, so a
/// pooled tile holds no native resource of its own.
/// </summary>
public sealed class WinoCalendarTile
{
    private const double CornerRadius = 5;
    private const double MonthDotSize = 8;
    private const double MinTextWidth = 24;

    private readonly CalendarLayoutCache _layouts;
    private readonly Func<CalendarPalette> _palette;
    private readonly TileRef _ref;
    private CalendarTileModel? _model;
    private CalendarPlacementKind _kind;
    private bool _hovered;
    private bool _dragGhost;
    private int _width = 1;
    private int _height = 1;
    private string? _accessibleName;
    private readonly List<IDisposable> _subscriptions = [];

    internal WinoCalendarTile(CalendarLayoutCache layouts, Func<CalendarPalette> palette)
    {
        WinoLiveObjects.Track(this);
        _layouts = layouts;
        _palette = palette;
        _ref = new TileRef { Value = this };
        // A drawing area has the generic role, whose accessible name GTK drops; the role can only be set at construction.
        using (var role = new GObject.Value(Gtk.AccessibleRole.Button))
            Root = (Gtk.DrawingArea)Gtk.DrawingArea.NewWithProperties([new GObject.ConstructArgument("accessible-role", role)]);
        Root.SetContentWidth(1);
        Root.SetContentHeight(1);
        Root.SetFocusable(true);
        Root.AddCssClass("wino-calendar-tile");
        var tile = _ref;
        Root.SetDrawFunc((_, cr, width, height) => tile.Value?.Draw(cr, width, height));
        AttachGestures();
    }

    /// <summary>
    /// Click and double click (button 1), right click, touch long press, hover and the keys of a focused tile. The press
    /// is claimed, so a tile press never reaches the strip (no slot click, no pan). Created once per tile, never per reuse:
    /// the item is read when the gesture fires.
    /// </summary>
    private void AttachGestures()
    {
        var root = Root;
        var click = Gtk.GestureClick.New();
        click.SetButton(0);
        root.AddController(click);
        _subscriptions.Add(GtkSignals.Connect<Gtk.GestureClick, Gtk.GestureClick.PressedSignalArgs>(h => click.OnPressed += h, h => click.OnPressed -= h, (gesture, args) =>
        {
            gesture.SetState(Gtk.EventSequenceState.Claimed);
            root.GrabFocus();
            if (gesture.GetCurrentButton() == 3) RaiseContextRequested(args.X, args.Y);
        }));
        _subscriptions.Add(GtkSignals.Connect<Gtk.GestureClick, Gtk.GestureClick.ReleasedSignalArgs>(h => click.OnReleased += h, h => click.OnReleased -= h, (gesture, args) =>
        {
            if (gesture.GetCurrentButton() != 1 || Item is null || DragStarted) return;
            if (args.NPress == 1) RaiseClicked(args.X, args.Y);
            else if (args.NPress == 2) RaiseDoubleClicked(args.X, args.Y);
        }));

        var longPress = Gtk.GestureLongPress.New();
        longPress.SetTouchOnly(true);
        root.AddController(longPress);
        _subscriptions.Add(GtkSignals.Connect<Gtk.GestureLongPress, Gtk.GestureLongPress.PressedSignalArgs>(h => longPress.OnPressed += h, h => longPress.OnPressed -= h,
            (_, args) => RaiseContextRequested(args.X, args.Y)));

        var motion = Gtk.EventControllerMotion.New();
        root.AddController(motion);
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerMotion, Gtk.EventControllerMotion.EnterSignalArgs>(h => motion.OnEnter += h, h => motion.OnEnter -= h, (_, _) => IsHovered = true));
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerMotion>(h => motion.OnLeave += h, h => motion.OnLeave -= h, (_, _) => IsHovered = false));

        var keys = Gtk.EventControllerKey.New();
        root.AddController(keys);
        _subscriptions.Add(GtkSignals.ConnectReturning<Gtk.EventControllerKey, Gtk.EventControllerKey.KeyPressedSignalArgs, bool>(h => keys.OnKeyPressed += h, h => keys.OnKeyPressed -= h,
            (_, args) => HandleKey(args.Keyval, args.State), false));
    }

    /// <summary>Return, Enter and Space open the event; Menu and Shift+F10 open its menu at the tile centre.</summary>
    private bool HandleKey(uint keyval, Gdk.ModifierType state)
    {
        if (Item is null) return false;
        var centreX = _width / 2.0;
        var centreY = _height / 2.0;
        var plain = (state & (Gdk.ModifierType.ControlMask | Gdk.ModifierType.AltMask | Gdk.ModifierType.SuperMask)) == 0;
        if (plain && keyval is Gdk.Constants.KEY_Return or Gdk.Constants.KEY_KP_Enter or Gdk.Constants.KEY_space or Gdk.Constants.KEY_KP_Space)
        {
            RaiseDoubleClicked(centreX, centreY);
            return true;
        }
        if (keyval == Gdk.Constants.KEY_Menu || (keyval == Gdk.Constants.KEY_F10 && (state & Gdk.ModifierType.ShiftMask) != 0))
        {
            RaiseContextRequested(centreX, centreY);
            return true;
        }
        return false;
    }

    public Gtk.DrawingArea Root { get; }

    public ICalendarItem? Item { get; internal set; }

    public DateOnly Date { get; internal set; }

    public CalendarPlacementKind Kind
    {
        get => _kind;
        internal set
        {
            if (_kind == value) return;
            _kind = value;
            Root.QueueDraw();
        }
    }

    public CalendarTileModel? Model
    {
        get => _model;
        internal set
        {
            if (Equals(_model, value)) return;
            if (_model?.Title != value?.Title || _model?.TimeRange != value?.TimeRange || _model?.Location != value?.Location) Root.SetTooltipText(Tooltip(value));
            _model = value;
            Root.QueueDraw();
        }
    }

    public CalendarLayoutRect Frame { get; private set; }

    public bool IsHovered
    {
        get => _hovered;
        set
        {
            if (_hovered == value) return;
            _hovered = value;
            Root.QueueDraw();
        }
    }

    public bool AllowsResize { get; set; }

    /// <summary>Set once a press on the tile moved past the drag threshold, so its release is no click.</summary>
    internal bool DragStarted { get; set; }

    /// <summary>A subscription of a surface part on this tile, disposed with the tile's own.</summary>
    internal void Own(IDisposable subscription) => _subscriptions.Add(subscription);

    /// <summary>The ghost of a drag: an accent outline over the tile.</summary>
    internal void SetDragAppearance(bool ghost)
    {
        if (_dragGhost == ghost) return;
        _dragGhost = ghost;
        Root.QueueDraw();
    }

    public event EventHandler<(double X, double Y)>? Clicked;
    public event EventHandler<(double X, double Y)>? DoubleClicked;
    public event EventHandler<(double X, double Y)>? ContextRequested;

    internal void RaiseClicked(double x, double y) => Clicked?.Invoke(this, (x, y));
    internal void RaiseDoubleClicked(double x, double y) => DoubleClicked?.Invoke(this, (x, y));
    internal void RaiseContextRequested(double x, double y) => ContextRequested?.Invoke(this, (x, y));

    internal void SetFrame(CalendarLayoutRect frame)
    {
        Frame = frame;
        var width = Math.Max(1, (int)Math.Round(frame.Width));
        var height = Math.Max(1, (int)Math.Round(frame.Height));
        if (width != _width)
        {
            _width = width;
            Root.SetContentWidth(width);
        }
        if (height != _height)
        {
            _height = height;
            Root.SetContentHeight(height);
        }
    }

    internal void SetAccessibleName(string? name)
    {
        if (string.Equals(name, _accessibleName, StringComparison.Ordinal)) return;
        _accessibleName = name;
        WinoA11y.Label(Root, name);
    }

    internal string? AccessibleName => _accessibleName;

    internal void Detach()
    {
        Item = null;
        Model = null;
        _hovered = false;
        Root.SetVisible(false);
    }

    /// <summary>The surface is gone: the draw function stops reaching this tile.</summary>
    internal void Release()
    {
        _ref.Value = null;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        Clicked = null;
        DoubleClicked = null;
        ContextRequested = null;
    }

    private static string? Tooltip(CalendarTileModel? model)
    {
        if (model is null) return null;
        var text = model.Title;
        if (!string.IsNullOrWhiteSpace(model.TimeRange)) text += "\n" + model.TimeRange;
        if (!string.IsNullOrWhiteSpace(model.Location)) text += "\n" + model.Location;
        return text;
    }

    private static bool IsBand(CalendarPlacementKind kind, CalendarTileModel model)
        => kind == CalendarPlacementKind.AllDay || (kind == CalendarPlacementKind.MonthCell && (model.IsMultiDay || model.IsAllDay));

    private void Draw(Cairo.Context cr, int width, int height)
    {
        if (_model is not { } model) return;
        var palette = _palette();
        bool compact = WinoCalendarTileAppearance.IsSingleLine(_kind);
        bool band = IsBand(_kind, model);
        bool monthEntry = _kind == CalendarPlacementKind.MonthCell && !band;
        bool cancelled = model.IsCancelled;
        bool response = !cancelled && model.NeedsResponse;
        bool past = !cancelled && !response && model.IsPast;
        bool contrast = palette.HighContrast;
        var fill = CalendarColor.From(model.Fill);

        CalendarColor text;
        CalendarColor detail;
        if (band)
        {
            double opacity = model.IsBusy ? 0.55 : _hovered ? 1 : 0.92;
            if (!contrast)
            {
                fill.WithAlpha(opacity).Use(cr);
                RoundedRect(cr, 0, 0, width, height, CornerRadius);
                cr.Fill();
            }
            text = contrast ? palette.Text : ReadableTextColor.PrefersDarkText(fill.R, fill.G, fill.B) ? new CalendarColor(0, 0, 0) : new CalendarColor(1, 1, 1);
            detail = text.WithAlpha(0.92);
        }
        else if (monthEntry)
        {
            if (!contrast && !cancelled && (_hovered || model.IsSelected))
            {
                fill.WithAlpha(model.IsSelected ? 0.22 : 0.1).Use(cr);
                RoundedRect(cr, 0, 0, width, height, CornerRadius);
                cr.Fill();
            }
            text = model.IsBusy || cancelled || past ? palette.SecondaryText : palette.Text;
            detail = palette.SecondaryText;
            fill.Use(cr);
            cr.Arc(4 + MonthDotSize / 2, height / 2.0, MonthDotSize / 2, 0, 2 * Math.PI);
            cr.Fill();
        }
        else
        {
            double tint = palette.IsDark ? 0.3 : 0.2;
            if (model.IsSelected) tint += 0.12;
            if (_hovered) tint += 0.06;
            if (model.IsBusy) tint *= 0.6;
            if (response || past) tint *= 0.5;
            if (!contrast && !cancelled)
            {
                fill.WithAlpha(tint).Use(cr);
                RoundedRect(cr, 0, 0, width, height, CornerRadius);
                cr.Fill();
            }
            var barAlpha = cancelled ? 0.35 : past ? 0.45 : model.IsBusy ? 0.5 : 1;
            DrawBar(cr, width, height, fill.WithAlpha(barAlpha), model.ShowAs);
            var label = palette.IsDark ? new CalendarColor(1, 1, 1) : new CalendarColor(0, 0, 0);
            text = contrast ? palette.Text : cancelled || past ? palette.SecondaryText : label.Blend(palette.IsDark ? 0.62 : 0.66, fill);
            detail = contrast ? palette.Text : label.Blend(0.5, fill);
            if (past) detail = detail.WithAlpha(0.7);
        }

        if (_dragGhost)
        {
            palette.Accent.Use(cr);
            cr.LineWidth = 2;
            RoundedRect(cr, 1, 1, width - 2, height - 2, CornerRadius - 1);
            cr.Stroke();
        }

        if (contrast || cancelled)
        {
            (contrast ? palette.Text : palette.Text.WithAlpha(0.3)).Use(cr);
            cr.LineWidth = 1;
            RoundedRect(cr, 0.5, 0.5, width - 1, height - 1, CornerRadius - 0.5);
            cr.Stroke();
        }
        else if (response)
        {
            text.Use(cr);
            cr.LineWidth = 1;
            cr.SetDash([4, 3], 0);
            RoundedRect(cr, 0.5, 0.5, width - 1, height - 1, CornerRadius - 0.5);
            cr.Stroke();
            cr.SetDash([], 0);
        }

        if (model.IsSelected)
        {
            (band ? (ReadableTextColor.PrefersDarkText(fill.R, fill.G, fill.B) ? new CalendarColor(0, 0, 0, 0.8) : new CalendarColor(1, 1, 1, 0.8)) : fill).Use(cr);
            cr.LineWidth = 2;
            RoundedRect(cr, 1, 1, width - 2, height - 2, CornerRadius - 1);
            cr.Stroke();
        }

        cr.Save();
        RoundedRect(cr, 0, 0, width, height, CornerRadius);
        cr.Clip();
        DrawContent(cr, width, height, model, compact, monthEntry, band, cancelled, text, detail);
        cr.Restore();
    }

    private static void DrawBar(Cairo.Context cr, int width, int height, CalendarColor color, CalendarItemShowAs showAs)
    {
        const double barWidth = WinoCalendarTileAppearance.StripeWidth;
        cr.Save();
        RoundedRect(cr, 0, 0, width, height, CornerRadius);
        cr.Clip();
        color.Use(cr);
        switch (showAs)
        {
            case CalendarItemShowAs.Free:
                cr.Rectangle(0, 0, 1.5, height);
                break;
            case CalendarItemShowAs.Tentative:
                for (double y = 2; y < height; y += 7) cr.Rectangle(0, y, barWidth, Math.Min(4, height - y));
                break;
            case CalendarItemShowAs.OutOfOffice:
                for (double y = 2; y < height; y += 4) cr.Rectangle(0, y, barWidth, Math.Min(2, height - y));
                break;
            default:
                cr.Rectangle(0, 0, barWidth, height);
                break;
        }
        cr.Fill();
        cr.Restore();
    }

    private void DrawContent(Cairo.Context cr, int width, int height, CalendarTileModel model, bool compact, bool monthEntry, bool band, bool cancelled,
        CalendarColor text, CalendarColor detail)
    {
        double glyphSize = WinoCalendarTileAppearance.GlyphSize;
        double textLeft = monthEntry ? 4 + MonthDotSize + 5 : band ? 7 : WinoCalendarTileAppearance.StripeWidth + 7;
        bool showRecurring = model.IsRecurring;
        bool showMultiDay = model.IsMultiDay;
        if (showRecurring && showMultiDay && width - 4 - WinoCalendarTileAppearance.GlyphColumnWidth(true, true) - textLeft < MinTextWidth) showRecurring = false;
        if ((showRecurring || showMultiDay) && width - 4 - WinoCalendarTileAppearance.GlyphColumnWidth(showRecurring, showMultiDay) - textLeft < MinTextWidth)
            showRecurring = showMultiDay = false;
        double textRight = width - 4 - WinoCalendarTileAppearance.GlyphColumnWidth(showRecurring, showMultiDay);
        double textWidth = Math.Max(0, textRight - textLeft);
        bool showText = textWidth >= MinTextWidth;

        var size = WinoCalendarTileAppearance.TitleFontSize(_kind, model.IsMultiDay, height);
        var titleStyle = monthEntry ? CalendarTextStyle.TileTitleMonth : size <= 11 ? CalendarTextStyle.TileTitle11 : size <= 12 ? CalendarTextStyle.TileTitle12 : CalendarTextStyle.TileTitle13;
        double y = 3;
        if (showText)
        {
            if (monthEntry && StartTime(model.TimeRange) is { } start)
            {
                var time = _layouts.Get(start, CalendarTextStyle.TileTime);
                var timeWidth = time.Width + 4;
                if (timeWidth < textWidth * 0.5)
                {
                    if (compact) y = (height - time.Height) / 2.0;
                    detail.Use(cr);
                    cr.MoveTo(textLeft, y);
                    PangoCairo.Functions.ShowLayout(cr, time.Layout);
                    textLeft += timeWidth;
                    textWidth -= timeWidth;
                }
            }
            var title = _layouts.Get(model.Title, titleStyle, (int)textWidth);
            if (compact) y = (height - title.Height) / 2.0;
            if (height >= title.Height)
            {
                text.Use(cr);
                cr.MoveTo(textLeft, y);
                PangoCairo.Functions.ShowLayout(cr, title.Layout);
                if (cancelled)
                {
                    cr.Rectangle(textLeft, Math.Round(y + title.Height / 2.0), Math.Min(textWidth, title.Width), 1);
                    cr.Fill();
                }
            }
            y += title.Height + 2;
        }

        if (showText && !compact && height >= WinoCalendarTileAppearance.DetailLinesMinHeight)
        {
            detail.Use(cr);
            foreach (var line in (string?[])[model.TimeRange, model.Location])
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var layout = _layouts.Get(line, CalendarTextStyle.TileDetail, (int)textWidth);
                if (y + layout.Height > height) continue;
                cr.MoveTo(textLeft, y);
                PangoCairo.Functions.ShowLayout(cr, layout.Layout);
                y += layout.Height + 2;
            }
        }

        if (showRecurring || showMultiDay)
        {
            text.Use(cr);
            double glyphX = width - 4 - glyphSize;
            if (showMultiDay)
            {
                ShowGlyph(cr, WinoIconGlyph.CalendarEventMuiltiDay, glyphX, compact, height);
                glyphX -= glyphSize + WinoCalendarTileAppearance.GlyphSpacing;
            }
            if (showRecurring) ShowGlyph(cr, WinoIconGlyph.CalendarEventRepeat, glyphX, compact, height);
        }
    }

    private void ShowGlyph(Cairo.Context cr, WinoIconGlyph glyph, double x, bool compact, int height)
    {
        var layout = _layouts.Get(WinoIcons.Glyph(glyph), CalendarTextStyle.Glyph);
        cr.MoveTo(x, compact ? (height - layout.Height) / 2.0 : 4);
        PangoCairo.Functions.ShowLayout(cr, layout.Layout);
    }

    private static string? StartTime(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;
        var separator = range.IndexOf(" – ", StringComparison.Ordinal);
        return separator > 0 ? range[..separator] : null;
    }

    internal static void RoundedRect(Cairo.Context cr, double x, double y, double width, double height, double radius)
    {
        radius = Math.Max(0, Math.Min(radius, Math.Min(width, height) / 2));
        cr.NewPath();
        cr.Arc(x + width - radius, y + radius, radius, -Math.PI / 2, 0);
        cr.Arc(x + width - radius, y + height - radius, radius, 0, Math.PI / 2);
        cr.Arc(x + radius, y + height - radius, radius, Math.PI / 2, Math.PI);
        cr.Arc(x + radius, y + radius, radius, Math.PI, 3 * Math.PI / 2);
        cr.ClosePath();
    }

#if DEBUG
    public enum DebugGesture { Click, DoubleClick, Context }

    public bool DebugKey(uint keyval) => HandleKey(keyval, 0);

    /// <summary>What a pointer gesture on the tile raises, at the tile centre.</summary>
    public void DebugRaise(DebugGesture gesture)
    {
        switch (gesture)
        {
            case DebugGesture.Click: RaiseClicked(_width / 2.0, _height / 2.0); break;
            case DebugGesture.DoubleClick: RaiseDoubleClicked(_width / 2.0, _height / 2.0); break;
            default: RaiseContextRequested(_width / 2.0, _height / 2.0); break;
        }
    }
#endif

    private sealed class TileRef
    {
        public WinoCalendarTile? Value;
    }
}
