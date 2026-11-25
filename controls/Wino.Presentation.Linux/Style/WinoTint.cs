using System.Text;
using Wino.Core.Domain.Presentation;

namespace Wino.Presentation.Linux.Style;

/// <summary>Gives a widget an arbitrary foreground colour through a generated CSS class, since GTK 4 has no inline style.</summary>
public static class WinoTint
{
    private const int MaximumColors = 512;

    private static readonly Dictionary<string, string> _classes = new(StringComparer.Ordinal);
    private static readonly StringBuilder _css = new();
    private static Gtk.CssProvider? _provider;

    /// <summary><c>#RRGGBB</c> or <c>#AARRGGBB</c> (case-insensitive) to <c>wino-tint-rrggbb</c>; null when not a colour or no display exists.</summary>
    public static string? ClassFor(string? hex)
    {
        if (Normalize(hex) is not { } color) return null;
        if (_classes.TryGetValue(color, out var known)) return known;
        if (_classes.Count >= MaximumColors || EnsureProvider() is not { } provider) return null;

        var name = WinoCssClass.TintPrefix + color;
        _css.Append('.').Append(name).Append(" { color: #").Append(color).Append("; }\n");
        provider.LoadFromString(_css.ToString());
        _classes[color] = name;
        return name;
    }

    /// <summary>
    /// <c>wino-fill-rrggbb-rrggbb</c>: background and text colour. A null <paramref name="foregroundHex"/> picks black or
    /// white, whichever reads on the background. Null when <paramref name="backgroundHex"/> is not a colour.
    /// </summary>
    public static string? FillClassFor(string? backgroundHex, string? foregroundHex)
    {
        if (Normalize(backgroundHex) is not { } background) return null;
        var foreground = Normalize(foregroundHex) ?? ReadableOn(background);
        var key = background + "-" + foreground;
        if (_classes.TryGetValue(key, out var known)) return known;
        if (_classes.Count >= MaximumColors || EnsureProvider() is not { } provider) return null;

        var name = WinoCssClass.FillPrefix + key;
        _css.Append('.').Append(name).Append(" { background-color: #").Append(background).Append("; color: #").Append(foreground).Append("; }\n");
        provider.LoadFromString(_css.ToString());
        _classes[key] = name;
        return name;
    }

    /// <summary>Swaps the tint class of <paramref name="widget"/>; <paramref name="current"/> holds the class it has now.</summary>
    public static void Apply(Gtk.Widget widget, ref string? current, string? hex)
    {
        var next = ClassFor(hex);
        if (string.Equals(next, current, StringComparison.Ordinal)) return;
        if (current is not null) widget.RemoveCssClass(current);
        if (next is not null) widget.AddCssClass(next);
        current = next;
    }

    private static Gtk.CssProvider? EnsureProvider()
    {
        if (_provider is not null) return _provider;
        if (Gdk.Display.GetDefault() is not { } display) return null;
        _provider = Gtk.CssProvider.New();
        Gtk.StyleContext.AddProviderForDisplay(display, _provider, WinoStyle.ApplicationPriority);
        return _provider;
    }

    private static string ReadableOn(string background)
    {
        var red = Convert.ToInt32(background[..2], 16) / 255.0;
        var green = Convert.ToInt32(background[2..4], 16) / 255.0;
        var blue = Convert.ToInt32(background[4..], 16) / 255.0;
        return ReadableTextColor.PrefersDarkText(red, green, blue) ? "000000" : "ffffff";
    }

    private static string? Normalize(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var value = hex.Trim();
        if (value.StartsWith('#')) value = value[1..];
        if (value.Length == 8) value = value[2..];
        if (value.Length != 6) return null;
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character)) return null;
        }
        return value.ToLowerInvariant();
    }
}
