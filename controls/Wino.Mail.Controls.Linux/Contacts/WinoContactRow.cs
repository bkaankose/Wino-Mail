using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Common;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.Contacts;

/// <summary>What one contact row shows. The page maps its contact into this snapshot.</summary>
public sealed record WinoContactRowModel(string? Name, string? SecondaryValue, string? SourceLabel, string? Address,
    bool IsFavorite, MailCategory[] Categories, Gdk.Texture? Picture);

/// <summary>
/// One contact in the contact list (Windows ContactTemplate, macOS WinoContactRowView): picture, name, secondary
/// value, source and category chips, and the favourite star. Every setter skips an unchanged value, because a bind
/// happens per scrolled row.
/// </summary>
public sealed class WinoContactRow : IDisposable
{
    public const int AvatarSize = 40;

    private readonly Gtk.Box _root = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
    private readonly WinoContactPicture _picture = new(AvatarSize);
    private readonly Gtk.Label _name = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _secondary = Gtk.Label.New(string.Empty);
    private readonly Gtk.Label _source = Gtk.Label.New(string.Empty);
    private readonly WinoCategoryChipRow _chips = new(16);
    private readonly Gtk.Button _star;
    private readonly Gtk.Label _starGlyph;
    private readonly IDisposable _starClicked;
    private WinoContactRowModel? _shown;
    private bool? _favorite;

    public WinoContactRow()
    {
        WinoLiveObjects.Track(this);
        _root.AddCssClass(WinoCssClass.ContactRow);
        _picture.Root.SetCanTarget(false);
        _root.Append(_picture.Root);

        var text = Gtk.Box.New(Gtk.Orientation.Vertical, 1);
        text.SetHexpand(true);
        text.SetValign(Gtk.Align.Center);
        text.SetCanTarget(false);
        _name.AddCssClass(WinoCssClass.ContactName);
        _name.SetXalign(0);
        _name.SetEllipsize(Pango.EllipsizeMode.End);
        _name.SetSingleLineMode(true);
        text.Append(_name);
        _secondary.AddCssClass("caption");
        _secondary.AddCssClass("dim-label");
        _secondary.SetXalign(0);
        _secondary.SetEllipsize(Pango.EllipsizeMode.End);
        _secondary.SetSingleLineMode(true);
        _secondary.SetVisible(false);
        text.Append(_secondary);
        var meta = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        _source.AddCssClass("caption");
        _source.AddCssClass("dim-label");
        _source.SetXalign(0);
        _source.SetEllipsize(Pango.EllipsizeMode.End);
        _source.SetSingleLineMode(true);
        meta.Append(_source);
        meta.Append(_chips.Root);
        text.Append(meta);
        _root.Append(text);

        _starGlyph = WinoIcons.Label(WinoIconGlyph.Star);
        _star = Gtk.Button.New();
        _star.SetChild(_starGlyph);
        _star.AddCssClass("flat");
        _star.SetValign(Gtk.Align.Center);
        _star.SetSizeRequest(28, 28);
        _root.Append(_star);
        _starClicked = GtkSignals.OnClicked(_star, () => FavoriteToggled?.Invoke());
        SetFavorite(false);
    }

    public Gtk.Widget Root => _root;

    public Gtk.Button FavoriteButton => _star;

    public event Action? FavoriteToggled;

    public void Apply(WinoContactRowModel model)
    {
        var previous = _shown;
        _shown = model;
        if (previous is null || !string.Equals(previous.Name, model.Name, StringComparison.Ordinal) || !string.Equals(previous.Address, model.Address, StringComparison.Ordinal))
        {
            _name.SetText(model.Name ?? model.Address ?? string.Empty);
            _picture.SetIdentity(model.Name, model.Address);
        }
        if (!ReferenceEquals(previous?.Picture, model.Picture)) _picture.Picture = model.Picture;
        if (previous is null || !string.Equals(previous.SecondaryValue, model.SecondaryValue, StringComparison.Ordinal))
        {
            var secondary = model.SecondaryValue ?? string.Empty;
            _secondary.SetText(secondary);
            _secondary.SetVisible(secondary.Length > 0);
            _root.SetTooltipText(secondary.Length > 0 ? secondary : null);
        }
        if (previous is null || !string.Equals(previous.SourceLabel, model.SourceLabel, StringComparison.Ordinal))
            _source.SetText(model.SourceLabel ?? string.Empty);
        _chips.Apply(model.Categories);
        SetFavorite(model.IsFavorite);
    }

    public void Dispose()
    {
        _starClicked.Dispose();
        FavoriteToggled = null;
    }

    private void SetFavorite(bool favorite)
    {
        if (_favorite == favorite) return;
        _favorite = favorite;
        WinoIcons.SetGlyph(_starGlyph, WinoIcons.Glyph(favorite ? WinoIconGlyph.StarFilled : WinoIconGlyph.Star));
        if (favorite)
        {
            _star.AddCssClass(WinoCssClass.FavoriteOn);
            _starGlyph.RemoveCssClass("dim-label");
        }
        else
        {
            _star.RemoveCssClass(WinoCssClass.FavoriteOn);
            _starGlyph.AddCssClass("dim-label");
        }
        WinoA11y.LabelWithTooltip(_star, favorite ? Translator.ContactAction_Unfavorite : Translator.ContactAction_Favorite);
    }
}

/// <summary>The letter header of a contact list section.</summary>
public static class WinoContactGroupHeader
{
    public static Gtk.Widget Create()
    {
        var label = Gtk.Label.New(string.Empty);
        label.AddCssClass(WinoCssClass.ContactGroupHeader);
        label.AddCssClass("heading");
        label.SetXalign(0);
        return label;
    }

    public static void Apply(Gtk.Widget root, string letter)
    {
        if (root is not Gtk.Label label || string.Equals(label.GetText(), letter, StringComparison.Ordinal)) return;
        label.SetText(letter);
        WinoA11y.Label(label, letter);
    }
}
