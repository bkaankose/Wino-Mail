using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings.Intelligence;

/// <summary>Small views shared by the Wino Intelligence page and its sub-pages (macOS IntelligenceSettingsViews).</summary>
internal static class IntelligenceViews
{
    public const string LearnMoreUrl = "https://www.winomail.app/intelligence";

    public static Gtk.Label Text(string? text, params string[] classes)
    {
        var label = Gtk.Label.New(text ?? string.Empty);
        label.SetXalign(0);
        label.SetWrap(true);
        label.SetWrapMode(Pango.WrapMode.WordChar);
        foreach (var css in classes) label.AddCssClass(css);
        return label;
    }

    /// <summary>A rounded status pill (Active, Limit reached, New); the tone is a libadwaita colour class.</summary>
    public static Gtk.Widget Pill(string text, string tone)
    {
        var label = Gtk.Label.New(text);
        label.AddCssClass("caption");
        label.AddCssClass(tone);
        label.SetMarginStart(10);
        label.SetMarginEnd(10);
        label.SetMarginTop(2);
        label.SetMarginBottom(2);
        var frame = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        frame.AddCssClass("card");
        frame.AddCssClass("wino-intelligence-pill");
        frame.Append(label);
        frame.SetValign(Gtk.Align.Center);
        WinoA11y.Label(frame, text);
        return frame;
    }

    /// <summary>The brand glyph on a subtle 44 px tile (macOS status and hero tiles).</summary>
    public static Gtk.Widget BrandTile(int size = 44, int glyph = 22)
    {
        var tile = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        tile.AddCssClass("wino-intelligence-tile");
        tile.SetSizeRequest(size, size);
        tile.SetValign(Gtk.Align.Center);
        tile.SetHalign(Gtk.Align.Start);
        tile.SetHexpand(false);
        var icon = new WinoIcon(WinoIconGlyph.WinoIntelligence, glyph, WinoIconMode.Colorful).Root;
        icon.SetHexpand(true);
        icon.SetHalign(Gtk.Align.Center);
        icon.SetValign(Gtk.Align.Center);
        tile.Append(icon);
        return tile;
    }

    /// <summary>A locked feature on the offer page: an insensitive row with a lock glyph.</summary>
    public static Adw.ActionRow LockedRow(string title, string description, WinoIconGlyph icon)
    {
        var row = WinoSettingsRows.Action(title, description, icon);
        var lockGlyph = WinoIcons.Label(WinoIconGlyph.LockClosed, 14);
        lockGlyph.AddCssClass("dim-label");
        WinoSettingsRows.Suffix(row, lockGlyph);
        row.SetSensitive(false);
        return row;
    }

    /// <summary>A flat accent link button (Windows HyperlinkButton).</summary>
    public static Gtk.Button Link(string title)
    {
        var button = Gtk.Button.NewWithLabel(title);
        button.AddCssClass("flat");
        button.AddCssClass("wino-link");
        button.AddCssClass("wino-intelligence-link");
        button.SetValign(Gtk.Align.Center);
        WinoA11y.Label(button, title);
        return button;
    }

    /// <summary>A 0..100 bar; <paramref name="error"/> draws the fill in the error colour.</summary>
    public static Gtk.LevelBar Bar(double value, bool error, int width = 200)
    {
        var bar = Gtk.LevelBar.New();
        bar.SetMinValue(0);
        bar.SetMaxValue(100);
        bar.SetSizeRequest(width, -1);
        bar.SetValign(Gtk.Align.Center);
        SetBar(bar, value, error);
        return bar;
    }

    public static void SetBar(Gtk.LevelBar bar, double value, bool error)
    {
        bar.SetValue(Math.Clamp(value, 0, 100));
        if (error) bar.AddCssClass("wino-bar-error");
        else bar.RemoveCssClass("wino-bar-error");
    }

    public static WinoInfoBarSeverity Severity(InfoBarMessageType type) => type switch
    {
        InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
        InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
        InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
        _ => WinoInfoBarSeverity.Informational
    };
}
