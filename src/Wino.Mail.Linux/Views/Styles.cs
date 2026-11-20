namespace Wino.Mail.Linux.Views;

/// <summary>The few rules libadwaita's style classes do not cover.</summary>
internal static class Styles
{
    private const string Css = """
        .wino-unread-badge { font-size: 0.8em; font-weight: 700; opacity: 0.75; }
        .wino-mail-row { padding: 8px 12px; }
        .wino-mail-row .wino-unread { font-weight: 700; }
        .wino-unread-dot { color: @accent_color; }
        .wino-group-header { padding: 14px 12px 4px 12px; font-weight: 700; font-size: 0.85em; opacity: 0.65; }
        .wino-reader-header { padding: 16px 20px 12px 20px; }
        .wino-attachment { padding: 4px 10px; border-radius: 6px; background-color: alpha(currentColor, 0.08); }
        """;

    private static bool _installed;

    public static void Install()
    {
        if (_installed || Gdk.Display.GetDefault() is not { } display) return;
        _installed = true;
        var provider = Gtk.CssProvider.New();
        provider.LoadFromString(Css);
        Gtk.StyleContext.AddProviderForDisplay(display, provider, 600);
    }
}
