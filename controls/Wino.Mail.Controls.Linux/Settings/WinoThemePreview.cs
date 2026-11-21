using System.Globalization;
using System.Text;
using Wino.Core.Domain.Enums;
using Wino.Presentation;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.Settings;

/// <summary>What a theme surface preview paints: the palette colours ("#RRGGBB" or "#AARRGGBB"), the accent and the wallpaper.</summary>
public sealed record WinoThemePreviewModel(
    string? Base, string? Navigation, string? Workspace, string? MailListHeader, string? ReadingPane,
    string? CalendarDefaultHour, string? CalendarWorkHour,
    string? AccentHex, string? WallpaperPath, ThemeWallpaperFit Fit, ThemeWallpaperAlignment Alignment);

/// <summary>
/// The miniature window of the theme editor (macOS ThemeSurfacePreviewView), built from boxes and styled by one stylesheet
/// per instance: wallpaper and base colour, navigation strip, list column with its header, and the reader or a calendar
/// grid. The part being edited is outlined in the accent. Main thread only; <see cref="Dispose"/> removes the stylesheet.
/// </summary>
public sealed class WinoThemePreview : IDisposable
{
    public const int PreviewWidth = 248;
    public const int PreviewHeight = 186;

    private static int _counter;

    private readonly string _class;
    private readonly Gtk.CssProvider _provider = Gtk.CssProvider.New();
    private bool _installed;

    public WinoThemePreview(CustomThemeColorKey highlightedKey, ThemeSurfaceScene scene, string accessibleLabel)
    {
        _class = "wino-surface-preview-" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture);
        var window = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
        window.AddCssClass("wino-surface-preview");
        window.AddCssClass(_class);
        window.AddCssClass("tp-window");
        window.SetSizeRequest(PreviewWidth, PreviewHeight);
        window.SetHalign(Gtk.Align.Start);
        window.SetValign(Gtk.Align.Start);
        window.SetOverflow(Gtk.Overflow.Hidden);
        window.SetFocusable(false);

        var navigation = Part("tp-navigation", 44, -1);
        navigation.SetMarginTop(6);
        navigation.SetMarginBottom(6);
        navigation.SetMarginStart(6);
        window.Append(navigation);

        var list = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
        list.AddCssClass("tp-workspace");
        list.SetSizeRequest(76, -1);
        list.SetMarginTop(6);
        list.SetMarginBottom(6);
        list.Append(Part("tp-mail-header", -1, 18));
        for (var line = 0; line < 3; line++)
        {
            var placeholder = Part("tp-line", -1, 8);
            placeholder.SetMarginStart(6);
            placeholder.SetMarginEnd(line == 2 ? 24 : 10);
            list.Append(placeholder);
        }
        window.Append(list);

        Gtk.Widget detail;
        if (scene == ThemeSurfaceScene.CalendarGrid)
        {
            var grid = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
            for (var hour = 0; hour < 6; hour++)
            {
                var strip = Part(hour is 2 or 3 or 4 ? "tp-hour-work" : "tp-hour-default", -1, -1);
                strip.SetVexpand(true);
                if (hour == 0 && highlightedKey == CustomThemeColorKey.CalendarDefaultHour) strip.AddCssClass("tp-highlight");
                if (hour == 2 && highlightedKey == CustomThemeColorKey.CalendarWorkHour) strip.AddCssClass("tp-highlight");
                grid.Append(strip);
            }
            detail = grid;
        }
        else
        {
            detail = Part("tp-reading", -1, -1);
        }
        detail.SetHexpand(true);
        detail.SetMarginTop(6);
        detail.SetMarginBottom(6);
        detail.SetMarginEnd(6);
        window.Append(detail);

        var highlighted = highlightedKey switch
        {
            CustomThemeColorKey.BaseSurface => window,
            CustomThemeColorKey.Navigation => navigation,
            CustomThemeColorKey.Workspace => list,
            CustomThemeColorKey.MailListHeader => list.GetFirstChild(),
            CustomThemeColorKey.ReadingPane => scene == ThemeSurfaceScene.CalendarGrid ? null : detail,
            _ => null
        };
        highlighted?.AddCssClass("tp-highlight");

        Root = window;
        WinoA11y.Label(Root, accessibleLabel);
        Root.AccessibleRole = Gtk.AccessibleRole.Img;
        WinoLiveObjects.Track(this);
    }

    public Gtk.Widget Root { get; }

    public void Update(WinoThemePreviewModel model)
    {
        if (!_installed)
        {
            if (Gdk.Display.GetDefault() is not { } display) return;
            Gtk.StyleContext.AddProviderForDisplay(display, _provider, WinoStyle.ThemePriority + 1);
            _installed = true;
        }
        _provider.LoadFromString(Build(model));
    }

    public void Dispose()
    {
        if (!_installed) return;
        _installed = false;
        if (Gdk.Display.GetDefault() is { } display) Gtk.StyleContext.RemoveProviderForDisplay(display, _provider);
    }

    private string Build(WinoThemePreviewModel model)
    {
        var css = new StringBuilder();
        var root = "." + _class;
        css.Append(root).Append(" { border-radius: 6px; background-color: ").Append(Color(model.Base, "@window_bg_color")).Append(';');
        if (ThemeCss.WallpaperUri(model.WallpaperPath) is { } uri)
        {
            var fit = model.Fit == ThemeWallpaperFit.Fit;
            css.Append(" background-image: url(\"").Append(uri).Append("\"); background-repeat: no-repeat; background-size: ")
                .Append(fit ? "contain" : "cover").Append("; background-position: ").Append(fit ? "center" : ThemeCss.Position(model.Alignment)).Append(';');
        }
        css.AppendLine(" }");
        Rule(css, root, "tp-navigation", model.Navigation, "4px");
        Rule(css, root, "tp-workspace", model.Workspace, "4px");
        Rule(css, root, "tp-mail-header", model.MailListHeader, "4px 4px 0 0");
        Rule(css, root, "tp-reading", model.ReadingPane, "4px");
        Rule(css, root, "tp-hour-default", model.CalendarDefaultHour, "2px");
        Rule(css, root, "tp-hour-work", model.CalendarWorkHour, "2px");
        css.Append(root).AppendLine(" .tp-line { background-color: alpha(currentColor, 0.18); border-radius: 4px; }");
        var accent = WinoRgba.FromHexString(model.AccentHex) is { } color ? ThemeCss.Rgba(color) : "@accent_color";
        css.Append(root).Append(" .tp-highlight, ").Append(root).Append(".tp-highlight { outline: 2px solid ").Append(accent).AppendLine("; outline-offset: -2px; }");
        return css.ToString();
    }

    private static void Rule(StringBuilder css, string root, string part, string? hex, string radius)
        => css.Append(root).Append(" .").Append(part).Append(" { background-color: ").Append(Color(hex, "transparent"))
            .Append("; border-radius: ").Append(radius).AppendLine("; }");

    private static string Color(string? hex, string fallback) => WinoRgba.FromHexString(hex) is { } color ? ThemeCss.Rgba(color) : fallback;

    private static Gtk.Box Part(string cssClass, int width, int height)
    {
        var part = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        part.AddCssClass(cssClass);
        part.SetSizeRequest(width, height);
        return part;
    }
}

/// <summary>
/// A colour button with the GTK colour dialog (macOS ThemeColorWell): <see cref="ColorPicked"/> raises the chosen colour as
/// "#RRGGBB" (or "#AARRGGBB" with alpha) 100 ms after the last change; <see cref="SetHex"/> shows a colour without raising it.
/// </summary>
public sealed class WinoColorWell : IDisposable
{
    private readonly bool _allowsAlpha;
    private readonly MainLoop.Throttle _throttle;
    private readonly IDisposable _notify;
    private bool _setting;

    public WinoColorWell(bool allowsAlpha, string accessibleLabel)
    {
        _allowsAlpha = allowsAlpha;
        var dialog = Gtk.ColorDialog.New();
        dialog.SetWithAlpha(allowsAlpha);
        dialog.SetTitle(accessibleLabel);
        Root = Gtk.ColorDialogButton.New(dialog);
        Root.SetValign(Gtk.Align.Center);
        WinoA11y.LabelWithTooltip(Root, accessibleLabel);
        _throttle = new MainLoop.Throttle(100, Raise);
        _notify = GtkSignals.OnNotify(Root, "rgba", () =>
        {
            if (!_setting) _throttle.Request();
        });
    }

    public Gtk.ColorDialogButton Root { get; }

    public event Action<string>? ColorPicked;

    public void SetHex(string? hex)
    {
        if (WinoRgba.FromHexString(hex) is not { } color) return;
        if (string.Equals(Hex(Root.GetRgba()), Hex(color), StringComparison.OrdinalIgnoreCase)) return;
        var rgba = new Gdk.RGBA
        {
            Red = color.R / 255f,
            Green = color.G / 255f,
            Blue = color.B / 255f,
            Alpha = _allowsAlpha ? (float)color.Alpha : 1f,
        };
        _setting = true;
        try { Root.SetRgba(rgba); }
        finally { _setting = false; }
    }

    /// <summary>Raises a pending colour now (before Save).</summary>
    public void Flush() => _throttle.Flush();

    public void Dispose()
    {
        _throttle.Dispose();
        _notify.Dispose();
        ColorPicked = null;
    }

    private void Raise() => ColorPicked?.Invoke(Hex(Root.GetRgba()));

    private string Hex(Gdk.RGBA rgba)
    {
        static byte Channel(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255), 0, 255);
        return Hex(new WinoRgba(Channel(rgba.Red), Channel(rgba.Green), Channel(rgba.Blue), rgba.Alpha));
    }

    private string Hex(WinoRgba color)
    {
        var rgb = string.Create(CultureInfo.InvariantCulture, $"{color.R:X2}{color.G:X2}{color.B:X2}");
        if (!_allowsAlpha) return "#" + rgb;
        var alpha = (byte)Math.Clamp((int)Math.Round(color.Alpha * 255), 0, 255);
        return string.Create(CultureInfo.InvariantCulture, $"#{alpha:X2}{rgb}");
    }
}
