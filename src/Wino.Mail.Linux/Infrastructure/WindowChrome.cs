using Wino.Platform.Linux.Desktop;

namespace Wino.Mail.Linux.Infrastructure;

/// <summary>
/// Window buttons for every header bar at once, through the GTK setting <c>gtk-decoration-layout</c>: none in the
/// Tiling style (the compositor closes windows; dialogs close with Escape), the desktop's own layout in Standard.
/// </summary>
internal static class WindowChrome
{
    private const string DecorationLayoutProperty = "gtk-decoration-layout";
    private const string NoButtons = ":";

    public static LinuxWindowStyle? Applied { get; private set; }

    public static void Apply(LinuxWindowStyle style)
    {
        if (Applied == style || Gtk.Settings.GetDefault() is not { } settings) return;
        if (style == LinuxWindowStyle.Tiling) settings.GtkDecorationLayout = NoButtons;
        else if (Applied is not null) settings.ResetProperty(DecorationLayoutProperty);
        Applied = style;
    }

    public static string DecorationLayout => Gtk.Settings.GetDefault()?.GtkDecorationLayout ?? string.Empty;
}
