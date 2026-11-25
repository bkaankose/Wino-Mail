using System.Globalization;
using Wino.Mail.Controls.Core;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.Common;

/// <summary>
/// A round avatar: initials on the shared 16-step palette (the same colour per address on every Wino head), a photo
/// when one is set, and an optional provider badge. It holds no handler, so recycled cells only set new values.
/// </summary>
public sealed class WinoContactPicture
{
    public const int ReaderSize = 40;
    public const int RowSize = 32;
    public const int ThreadChildSize = 26;

    private const string SquareClass = "wino-avatar-square";
    private const string PhotoClass = "wino-avatar-photo";
    private const string BadgeClass = "wino-avatar-badge";
    private static readonly int[] Sizes = [16, 20, 22, 24, 26, 28, 32, 36, 40, 48, 56, 64, 72, 96, 128];

    private readonly Gtk.Overlay _root;
    private readonly Gtk.Overlay _disc;
    private readonly Gtk.Label _initials;
    private readonly Gtk.Picture _photo;
    private readonly Gtk.Box _badge;
    private readonly Gtk.Label _badgeGlyph;
    private int _size;
    private int _paletteIndex = -1;
    private bool _isSquare;
    private Gdk.Texture? _picture;

    public WinoContactPicture(int size = RowSize)
    {
        _root = Gtk.Overlay.New();
        _root.AccessibleRole = Gtk.AccessibleRole.Img;
        _root.SetHalign(Gtk.Align.Center);
        _root.SetValign(Gtk.Align.Center);
        _root.SetHexpand(false);

        _disc = Gtk.Overlay.New();
        _disc.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _disc.AddCssClass(WinoCssClass.Avatar);
        _disc.SetOverflow(Gtk.Overflow.Hidden);

        _initials = Gtk.Label.New(string.Empty);
        _initials.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _initials.SetHalign(Gtk.Align.Center);
        _initials.SetValign(Gtk.Align.Center);
        _disc.SetChild(_initials);

        _photo = Gtk.Picture.New();
        _photo.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _photo.SetContentFit(Gtk.ContentFit.Cover);
        _photo.SetCanShrink(true);
        _photo.SetVisible(false);
        _disc.AddOverlay(_photo);
        _root.SetChild(_disc);

        _badge = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _badge.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _badge.AddCssClass(BadgeClass);
        _badge.SetHalign(Gtk.Align.End);
        _badge.SetValign(Gtk.Align.End);
        _badge.SetVisible(false);
        _badgeGlyph = WinoIcons.Label(string.Empty, BadgeSize(size));
        _badge.Append(_badgeGlyph);
        _root.AddOverlay(_badge);

        SetPalette(ContactPictureAppearance.FallbackPaletteIndex);
        _initials.SetText(ContactPictureAppearance.Initials(null));
        ApplySize(size);
    }

    public Gtk.Widget Root => _root;

    public int Size
    {
        get => _size;
        set
        {
            if (_size == value) return;
            ApplySize(value);
        }
    }

    public bool IsSquare
    {
        get => _isSquare;
        set
        {
            if (_isSquare == value) return;
            _isSquare = value;
            if (value) _disc.AddCssClass(SquareClass);
            else _disc.RemoveCssClass(SquareClass);
        }
    }

    public Gdk.Texture? Picture
    {
        get => _picture;
        set
        {
            if (ReferenceEquals(_picture, value)) return;
            _picture = value;
            _photo.SetPaintable(value);
            _photo.SetVisible(value is not null);
            _initials.SetVisible(value is null);
            if (value is null) _disc.RemoveCssClass(PhotoClass);
            else _disc.AddCssClass(PhotoClass);
        }
    }

    /// <summary>Initials from <paramref name="displayName"/>, colour from <paramref name="colorKey"/> (the address); either falls back to the other.</summary>
    public void SetIdentity(string? displayName, string? colorKey)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? colorKey : displayName;
        _initials.SetText(ContactPictureAppearance.Initials(name));
        SetPalette(ContactPictureAppearance.PaletteIndex(string.IsNullOrWhiteSpace(colorKey) ? displayName : colorKey));
        WinoA11y.Label(_root, name);
    }

    public void SetIdentity(IContactPicture? contact) => SetIdentity(contact?.Name, contact?.Address);

    /// <summary>A WinoIcons glyph at the bottom end; null or empty hides the badge.</summary>
    public void SetBadge(string? glyph)
    {
        WinoIcons.SetGlyph(_badgeGlyph, glyph ?? string.Empty);
        _badge.SetVisible(!string.IsNullOrEmpty(glyph));
    }

    public void Clear()
    {
        SetIdentity(null, null);
        Picture = null;
        SetBadge(null);
    }

    private void SetPalette(int index)
    {
        if (index == _paletteIndex) return;
        if (_paletteIndex >= 0) _disc.RemoveCssClass(WinoStyle.AvatarClass(_paletteIndex));
        _paletteIndex = index;
        _disc.AddCssClass(WinoStyle.AvatarClass(index));
    }

    private void ApplySize(int size)
    {
        if (_size > 0)
        {
            _disc.RemoveCssClass(SizeClass(_size));
            _badgeGlyph.RemoveCssClass(WinoIcons.SizeClass(BadgeSize(_size)));
        }
        _size = Math.Max(1, size);
        _root.SetSizeRequest(_size, _size);
        _disc.SetSizeRequest(_size, _size);
        _disc.AddCssClass(SizeClass(_size));
        _badgeGlyph.AddCssClass(WinoIcons.SizeClass(BadgeSize(_size)));
    }

    private static int BadgeSize(int size) => Math.Max(10, (int)Math.Round(size * 0.4));

    private static string SizeClass(int size)
    {
        var supported = Sizes[0];
        foreach (var candidate in Sizes)
        {
            if (candidate > size) break;
            supported = candidate;
        }
        return "wino-avatar-s" + supported.ToString(CultureInfo.InvariantCulture);
    }
}
