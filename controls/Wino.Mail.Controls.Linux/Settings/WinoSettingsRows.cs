using System.Runtime.CompilerServices;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Settings;

/// <summary>
/// Unbound settings rows. Titles and subtitles are plain text (translations contain <c>&amp;</c>), descriptions wrap,
/// and the Wino glyph sits in a 28 px prefix slot. <see cref="SettingsBinder"/> builds its bound rows on these.
/// </summary>
public static class WinoSettingsRows
{
    public const int GlyphSize = 16;
    private const int GlyphSlot = 28;

    private static readonly ConditionalWeakTable<Gtk.Widget, Gtk.Widget> Prefixes = new();

    public static Adw.ActionRow Action(string title, string? description = null, WinoIconGlyph icon = WinoIconGlyph.None)
        => Decorate(Adw.ActionRow.New(), title, description, icon);

    public static Adw.SwitchRow Switch(string title, string? description = null, WinoIconGlyph icon = WinoIconGlyph.None)
        => Decorate(Adw.SwitchRow.New(), title, description, icon);

    public static Adw.ComboRow Combo(string title, string? description = null, WinoIconGlyph icon = WinoIconGlyph.None)
    {
        var row = Decorate(Adw.ComboRow.New(), title, description, icon);
        row.SetEnableSearch(false);
        return row;
    }

    public static Gtk.SpinButton SpinButton(string accessibleName, int minimum, int maximum, int step = 1)
    {
        var spin = Gtk.SpinButton.NewWithRange(minimum, Math.Max(minimum, maximum), Math.Max(1, step));
        spin.SetDigits(0);
        spin.SetNumeric(true);
        spin.SetSnapToTicks(step > 1);
        spin.SetValign(Gtk.Align.Center);
        WinoA11y.Label(spin, accessibleName);
        return spin;
    }

    public static Adw.EntryRow Entry(string title)
    {
        var row = Adw.EntryRow.New();
        row.SetUseMarkup(false);
        row.SetTitle(title);
        return row;
    }

    public static Adw.PasswordEntryRow Password(string title)
    {
        var row = Adw.PasswordEntryRow.New();
        row.SetUseMarkup(false);
        row.SetTitle(title);
        return row;
    }

    public static Adw.ExpanderRow Expander(string title, string? description = null, WinoIconGlyph icon = WinoIconGlyph.None,
        bool expanded = false, params Gtk.Widget?[] rows)
    {
        var row = Adw.ExpanderRow.New();
        row.SetUseMarkup(false);
        row.SetTitle(title);
        if (!string.IsNullOrWhiteSpace(description)) row.SetSubtitle(description);
        row.SetSubtitleLines(0);
        SetGlyph(row, icon);
        foreach (var child in rows)
        {
            if (child is not null) row.AddRow(child);
        }
        row.SetExpanded(expanded);
        return row;
    }

    /// <summary>A full-width button row. libadwaita draws its own content, so it takes no Wino glyph.</summary>
    public static Adw.ButtonRow Button(string title, bool destructive = false, bool suggested = false)
    {
        var row = Adw.ButtonRow.New();
        row.SetUseMarkup(false);
        row.SetTitle(title);
        if (destructive) row.AddCssClass("destructive-action");
        if (suggested) row.AddCssClass("suggested-action");
        return row;
    }

    /// <summary>Free-form content inside the boxed list; the row itself is neither activatable nor selectable.</summary>
    public static Adw.PreferencesRow Custom(Gtk.Widget child, string? accessibleName = null)
    {
        var row = Adw.PreferencesRow.New();
        row.SetUseMarkup(false);
        row.SetActivatable(false);
        row.SetSelectable(false);
        row.SetFocusable(false);
        if (!string.IsNullOrEmpty(accessibleName))
        {
            row.SetTitle(accessibleName);
            WinoA11y.Label(row, accessibleName);
        }
        child.SetMarginTop(12);
        child.SetMarginBottom(12);
        child.SetMarginStart(12);
        child.SetMarginEnd(12);
        row.SetChild(child);
        return row;
    }

    public static Gtk.Label Caption(string? text)
    {
        var label = Gtk.Label.New(text ?? string.Empty);
        label.AddCssClass("dim-label");
        label.SetWrap(true);
        label.SetWrapMode(Pango.WrapMode.WordChar);
        label.SetXalign(0);
        label.SetHalign(Gtk.Align.Fill);
        return label;
    }

    /// <summary>Replaces the glyph prefix of an action, expander or entry row; <see cref="WinoIconGlyph.None"/> removes it.</summary>
    public static void SetGlyph(Adw.PreferencesRow row, WinoIconGlyph icon)
    {
        if (Prefixes.TryGetValue(row, out var previous))
        {
            Prefixes.Remove(row);
            RemovePrefix(row, previous);
        }
        if (icon == WinoIconGlyph.None) return;

        var slot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        slot.SetHomogeneous(true);
        slot.SetSizeRequest(GlyphSlot, -1);
        slot.SetValign(Gtk.Align.Center);
        var glyph = new WinoIcon(icon, GlyphSize).Root;
        glyph.SetHalign(Gtk.Align.Center);
        slot.Append(glyph);
        switch (row)
        {
            case Adw.ActionRow action: action.AddPrefix(slot); break;
            case Adw.ExpanderRow expander: expander.AddPrefix(slot); break;
            case Adw.EntryRow entry: entry.AddPrefix(slot); break;
            default: return;
        }
        Prefixes.AddOrUpdate(row, slot);
    }

    public static T Suffix<T>(Adw.ActionRow row, T widget) where T : Gtk.Widget
    {
        widget.SetValign(Gtk.Align.Center);
        row.AddSuffix(widget);
        return widget;
    }

    public static T Suffix<T>(Adw.ExpanderRow row, T widget) where T : Gtk.Widget
    {
        widget.SetValign(Gtk.Align.Center);
        row.AddSuffix(widget);
        return widget;
    }

    public static Gtk.Button ChevronButton(string accessibleName)
    {
        var button = Gtk.Button.NewFromIconName("go-next-symbolic");
        button.AddCssClass("flat");
        button.SetValign(Gtk.Align.Center);
        WinoA11y.Label(button, accessibleName);
        return button;
    }

    public static Gtk.Button GlyphButton(WinoIconGlyph icon, string accessibleName)
    {
        var button = WinoIcons.Button(icon, accessibleName);
        button.SetValign(Gtk.Align.Center);
        return button;
    }

    private static T Decorate<T>(T row, string title, string? description, WinoIconGlyph icon) where T : Adw.ActionRow
    {
        row.SetUseMarkup(false);
        row.SetTitle(title);
        if (!string.IsNullOrWhiteSpace(description)) row.SetSubtitle(description);
        row.SetSubtitleLines(0);
        SetGlyph(row, icon);
        return row;
    }

    private static void RemovePrefix(Adw.PreferencesRow row, Gtk.Widget prefix)
    {
        switch (row)
        {
            case Adw.ActionRow action: action.Remove(prefix); break;
            case Adw.ExpanderRow expander: expander.Remove(prefix); break;
            case Adw.EntryRow entry: entry.Remove(prefix); break;
        }
    }
}
