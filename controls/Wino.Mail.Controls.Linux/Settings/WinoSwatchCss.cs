using System.Globalization;
using System.Text;

namespace Wino.Mail.Controls.Linux.Settings;

/// <summary>
/// One process-wide stylesheet for colour swatches and theme previews: each solid colour or gradient gets a class
/// name, and the stylesheet is regenerated when a new one is asked for. Only hex digits reach the CSS. Main thread only.
/// </summary>
public static class WinoSwatchCss
{
    private const int Priority = 600;

    // Static rules of the theme tiles: the selection badge shows through CSS on the checked toggle, so tiles need no signal.
    private const string Base = """
        .wino-swatch { min-width: 32px; min-height: 32px; padding: 0; border-radius: 999px; box-shadow: inset 0 0 0 1px alpha(currentColor, 0.15); }
        .wino-swatch .wino-swatch-check { opacity: 0; color: white; }
        .wino-swatch:checked .wino-swatch-check { opacity: 1; }
        .wino-swatch:checked { outline: 2px solid @accent_color; outline-offset: 2px; }
        .wino-theme-tile { padding: 6px; border-radius: 12px; }
        .wino-theme-preview { border-radius: 8px; background-color: @window_bg_color; box-shadow: inset 0 0 0 1px alpha(currentColor, 0.12); }
        .wino-theme-mini-sidebar { background-color: alpha(@sidebar_bg_color, 0.85); border-radius: 6px 0 0 6px; }
        .wino-theme-mini-content { background-color: alpha(@view_bg_color, 0.9); border-radius: 4px; }
        .wino-theme-check { min-width: 20px; min-height: 20px; border-radius: 999px; background-color: @accent_bg_color; color: @accent_fg_color; opacity: 0; }
        .wino-theme-tile:checked .wino-theme-check { opacity: 1; }
        .wino-theme-tile:checked .wino-theme-preview { box-shadow: inset 0 0 0 2px @accent_color; }
        """;

    private static readonly Dictionary<string, string> Rules = new(StringComparer.Ordinal);
    private static Gtk.CssProvider? _provider;

    /// <summary>The class for <paramref name="hex"/> (<c>#rrggbb</c> or <c>rrggbb</c>); empty for anything else.</summary>
    public static string Solid(string? hex)
    {
        if (Normalize(hex) is not { } value) return string.Empty;
        var name = "wino-swatch-" + value;
        Add(name, $".{name} {{ background-color: #{value}; }}");
        return name;
    }

    /// <summary>The class for a 162° gradient between two RGB values, the angle F-08's theme backdrop uses.</summary>
    public static string Gradient(uint start, uint end)
    {
        var from = (start & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture);
        var to = (end & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture);
        var name = $"wino-gradient-{from}-{to}";
        Add(name, $".{name} {{ background-image: linear-gradient(162deg, #{from}, #{to}); }}");
        return name;
    }

    /// <summary>Installs the static rules; the factory methods call it themselves.</summary>
    public static void EnsureInstalled()
    {
        if (_provider is not null) return;
        if (Gdk.Display.GetDefault() is not { } display) return;
        _provider = Gtk.CssProvider.New();
        _provider.LoadFromString(Build());
        Gtk.StyleContext.AddProviderForDisplay(display, _provider, Priority);
    }

    private static void Add(string name, string rule)
    {
        EnsureInstalled();
        if (!Rules.TryAdd(name, rule)) return;
        _provider?.LoadFromString(Build());
    }

    private static string Build()
    {
        var css = new StringBuilder(Base);
        foreach (var rule in Rules.Values) css.AppendLine(rule);
        return css.ToString();
    }

    private static string? Normalize(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var value = hex.Trim().TrimStart('#');
        if (value.Length == 8) value = value[2..];
        if (value.Length != 6) return null;
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character)) return null;
        }
        return value.ToLowerInvariant();
    }
}
