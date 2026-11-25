using System.Globalization;
using System.Text;
using Wino.Core.Domain.Enums;

namespace Wino.Presentation.Linux.Style;

/// <summary>The Wino stylesheet for GTK: token variables from <see cref="WinoTokens"/>, the avatar palette and the static rules in <c>wino.css</c>.</summary>
public static class WinoStyle
{
    public const uint ApplicationPriority = 600;
    public const uint ThemePriority = 601;

    private const string ResourceName = "Wino.Presentation.Linux.Style.wino.css";

    private static Gtk.CssProvider? _provider;

    public static string Css { get; } = BuildCss();

    /// <summary>Loads the stylesheet for the default display once; returns false while no display exists, so a later call can retry.</summary>
    public static bool Install(Action<string>? parsingError = null)
    {
        if (_provider != null) return true;
        if (Gdk.Display.GetDefault() is not { } display) return false;

        var provider = Gtk.CssProvider.New();
        if (parsingError != null) provider.OnParsingError += (_, args) => parsingError(args.Error.Message);
        provider.LoadFromString(Css);
        Gtk.StyleContext.AddProviderForDisplay(display, provider, ApplicationPriority);
        _provider = provider;
        return true;
    }

    public static string DensityClass(MailListDisplayMode mode) => mode switch
    {
        MailListDisplayMode.Spacious => WinoCssClass.DensitySpacious,
        MailListDisplayMode.Compact => WinoCssClass.DensityCompact,
        _ => WinoCssClass.DensityMedium
    };

    /// <summary>Sets the density class on a list or row; descendants with <see cref="WinoCssClass.MailRow"/> take its padding.</summary>
    public static void SetDensity(Gtk.Widget widget, MailListDisplayMode mode)
    {
        var target = DensityClass(mode);
        foreach (var name in (string[])[WinoCssClass.DensitySpacious, WinoCssClass.DensityMedium, WinoCssClass.DensityCompact])
        {
            if (name != target) widget.RemoveCssClass(name);
        }
        widget.AddCssClass(target);
    }

    public static string AvatarClass(int paletteIndex)
        => WinoCssClass.AvatarPrefix + Math.Clamp(paletteIndex, 0, WinoTokens.AvatarPalette.Count - 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Colours <paramref name="widget"/> with the avatar palette step of <paramref name="key"/>, the same step every Wino head picks.</summary>
    public static void SetAvatar(Gtk.Widget widget, string? key)
    {
        var target = AvatarClass(WinoTokens.AvatarPaletteIndex(key));
        for (var index = 0; index < WinoTokens.AvatarPalette.Count; index++)
        {
            var name = AvatarClass(index);
            if (name != target) widget.RemoveCssClass(name);
        }
        widget.AddCssClass(WinoCssClass.Avatar);
        widget.AddCssClass(target);
    }

    private static string BuildCss()
    {
        var css = new StringBuilder();
        css.AppendLine(":root {");
        Variable(css, "space-1", WinoTokens.Space1);
        Variable(css, "space-2", WinoTokens.Space2);
        Variable(css, "space-3", WinoTokens.Space3);
        Variable(css, "space-4", WinoTokens.Space4);
        Variable(css, "space-6", WinoTokens.Space6);
        Variable(css, "space-8", WinoTokens.Space8);
        Variable(css, "control-radius", WinoTokens.ControlRadius);
        Variable(css, "group-radius", WinoTokens.GroupRadius);
        Variable(css, "popover-radius", WinoTokens.PopoverRadius);
        Variable(css, "sidebar-row-height", WinoTokens.SidebarRowHeight);
        Variable(css, "settings-row-min-height", WinoTokens.SettingsRowMinHeight);
        Variable(css, "zone-gutter", WinoTokens.ZoneGutter);
        Variable(css, "zone-radius", WinoTokens.ZoneRadius);
        Variable(css, "row-padding", WinoTokens.Space2);
        css.AppendLine("}");

        Density(css, WinoCssClass.DensitySpacious, WinoTokens.Space3);
        Density(css, WinoCssClass.DensityMedium, WinoTokens.Space2);
        Density(css, WinoCssClass.DensityCompact, WinoTokens.Space1);

        for (var index = 0; index < WinoTokens.AvatarPalette.Count; index++)
        {
            css.Append('.').Append(AvatarClass(index)).Append(" { background-color: ")
                .Append(WinoTokens.AvatarPalette[index]).AppendLine("; color: #ffffff; }");
        }

        css.AppendLine(ReadStaticCss());
        return css.ToString();
    }

    private static void Variable(StringBuilder css, string name, double pixels)
        => css.Append("  --wino-").Append(name).Append(": ").Append(Pixels(pixels)).AppendLine(";");

    private static void Density(StringBuilder css, string className, double padding)
        => css.Append('.').Append(className).Append(" { --wino-row-padding: ").Append(Pixels(padding)).AppendLine("; }");

    private static string Pixels(double value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "px";

    private static string ReadStaticCss()
    {
        using var stream = typeof(WinoStyle).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded stylesheet {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
