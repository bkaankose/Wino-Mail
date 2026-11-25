using System.Globalization;
using System.Text;
using Wino.Presentation;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// The theme editor's swatch stylesheet: one display provider owned by the page, a class per swatch slot whose colour is
/// rewritten in place, reloaded once per main-loop turn. Translucent colours sit over a checkerboard. Main thread only.
/// </summary>
internal sealed class ThemeEditorCss : IDisposable
{
    private readonly Dictionary<string, string> _colors = new(StringComparer.Ordinal);
    private readonly Gtk.CssProvider _provider = Gtk.CssProvider.New();
    private readonly MainLoop.Coalescer _reload;
    private readonly string _prefix;
    private bool _installed;
    private int _next;

    private static int _instances;

    public ThemeEditorCss()
    {
        _prefix = "wino-te" + Interlocked.Increment(ref _instances).ToString(CultureInfo.InvariantCulture) + "-";
        _reload = new MainLoop.Coalescer(Reload);
    }

    /// <summary>A new swatch slot; style a widget with the returned class and colour it with <see cref="Set"/>.</summary>
    public string NewSlot() => _prefix + (_next++).ToString(CultureInfo.InvariantCulture);

    public void Set(string slot, string? hex)
    {
        var color = WinoRgba.FromHexString(hex) is { } rgba ? ThemeCss.Rgba(rgba) : "transparent";
        if (_colors.TryGetValue(slot, out var current) && current == color) return;
        _colors[slot] = color;
        _reload.Request();
    }

    public void Dispose()
    {
        _reload.Dispose();
        if (!_installed) return;
        _installed = false;
        if (Gdk.Display.GetDefault() is { } display) Gtk.StyleContext.RemoveProviderForDisplay(display, _provider);
    }

    private void Reload()
    {
        if (!_installed)
        {
            if (Gdk.Display.GetDefault() is not { } display) return;
            Gtk.StyleContext.AddProviderForDisplay(display, _provider, WinoStyle.ThemePriority + 1);
            _installed = true;
        }
        var css = new StringBuilder();
        css.AppendLine(".wino-te-focal { min-width: 20px; min-height: 20px; padding: 0; border-radius: 999px; background: alpha(black, 0.35); box-shadow: 0 0 0 2px alpha(white, 0.85); }");
        css.AppendLine(".wino-te-focal:checked { background: @accent_bg_color; box-shadow: 0 0 0 3px white; }");
        css.AppendLine(".wino-te-swatch { border-radius: 6px; box-shadow: inset 0 0 0 1px alpha(currentColor, 0.2); background-color: #ffffff; background-size: 8px 8px; background-image: linear-gradient(45deg, #cccccc 25%, transparent 25%, transparent 75%, #cccccc 75%), linear-gradient(45deg, #cccccc 25%, transparent 25%, transparent 75%, #cccccc 75%); background-position: 0 0, 4px 4px; }");
        foreach (var (slot, color) in _colors)
            css.Append('.').Append(slot).Append(" { box-shadow: inset 0 0 0 1px alpha(currentColor, 0.2), inset 0 0 0 100px ").Append(color).AppendLine("; }");
        _provider.LoadFromString(css.ToString());
    }
}
