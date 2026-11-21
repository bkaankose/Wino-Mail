using System.Text;

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
        if (_classes.Count >= MaximumColors) return null;
        if (_provider is null)
        {
            if (Gdk.Display.GetDefault() is not { } display) return null;
            _provider = Gtk.CssProvider.New();
            Gtk.StyleContext.AddProviderForDisplay(display, _provider, WinoStyle.ApplicationPriority);
        }

        var name = WinoCssClass.TintPrefix + color;
        _css.Append('.').Append(name).Append(" { color: #").Append(color).Append("; }\n");
        _provider.LoadFromString(_css.ToString());
        _classes[color] = name;
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
