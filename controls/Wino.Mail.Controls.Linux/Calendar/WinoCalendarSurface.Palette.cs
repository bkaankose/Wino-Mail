using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>The surface colours as plain components, computed once per theme or accent change (macOS values).</summary>
internal sealed class CalendarPalette
{
    public CalendarColor Line, MinorLine, HeaderFill, AllDayFill, WeekendFill, OffHours, Now, Accent, AccentForeground, Text, SecondaryText, TertiaryText, Selection;
    public bool IsDark;
    public bool HighContrast;

    public static CalendarPalette Create(Gtk.Widget widget)
    {
        var styles = Adw.StyleManager.GetDefault();
        var dark = styles.GetDark();
        widget.GetColor(out var foreground);
        var text = new CalendarColor(foreground.Red, foreground.Green, foreground.Blue);
        CalendarColor accent;
        if (WinoAppearance.Accent is { } wino) accent = CalendarColor.From(wino);
        else
        {
            var rgba = styles.GetAccentColorRgba();
            accent = new CalendarColor(rgba.Red, rgba.Green, rgba.Blue);
        }
        var light = dark ? 1.0 : 0.0;
        return new CalendarPalette
        {
            IsDark = dark,
            HighContrast = styles.GetHighContrast(),
            Text = text,
            SecondaryText = text.WithAlpha(0.55),
            TertiaryText = text.WithAlpha(0.4),
            Line = new CalendarColor(light, light, light, dark ? 0.09 : 0.08),
            MinorLine = new CalendarColor(light, light, light, dark ? 0.045 : 0.04),
            HeaderFill = new CalendarColor(light, light, light, dark ? 0.07 : 0.045),
            AllDayFill = new CalendarColor(light, light, light, dark ? 0.035 : 0.0225),
            WeekendFill = new CalendarColor(light, light, light, dark ? 0.05 : 0.035),
            OffHours = new CalendarColor(0, 0, 0, dark ? 0.10 : 0.02),
            Now = new CalendarColor(0xD6 / 255.0, 0x30 / 255.0, 0x31 / 255.0),
            Accent = accent,
            AccentForeground = Wino.Core.Domain.Presentation.ReadableTextColor.PrefersDarkText(accent.R, accent.G, accent.B) ? new CalendarColor(0, 0, 0) : new CalendarColor(1, 1, 1),
            Selection = accent.WithAlpha(0.18)
        };
    }
}

public sealed partial class WinoCalendarSurface
{
    private CalendarPalette? _palette;

    internal CalendarPalette Palette => _palette ??= CalendarPalette.Create(Root);

    private void SubscribePalette()
    {
        var styles = Adw.StyleManager.GetDefault();
        foreach (var property in (string[])["dark", "accent-color-rgba", "high-contrast"])
            Own(GtkSignals.OnNotify(styles, property, InvalidatePalette));
        WinoAppearance.Changed.Add(this, static surface => surface.InvalidatePalette());
        Own(new ActionDisposable(() => WinoAppearance.Changed.Remove(this)));
    }

    private void InvalidatePalette()
    {
        if (_disposed) return;
        _palette = null;
        QueueDrawAll();
        foreach (var tile in _timedLayer.Active) tile.Root.QueueDraw();
        foreach (var tile in _allDayLayer.Active) tile.Root.QueueDraw();
        foreach (var tile in _monthLayer.Active) tile.Root.QueueDraw();
    }
}
