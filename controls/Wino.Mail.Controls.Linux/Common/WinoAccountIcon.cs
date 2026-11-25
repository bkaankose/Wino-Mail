using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Presentation.Controls;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Controls.Linux.Common;

/// <summary>
/// The account's profile picture in a circle when one is stored, otherwise its provider glyph (the brand mark, or its
/// mono twin tinted with the account colour). Shell rows use 28, compact rows 22.
/// </summary>
public sealed class WinoAccountIcon
{
    /// <summary>Provider glyphs draw inside the em box; this scale matches a picture's drawn size (Windows and macOS use 0.8).</summary>
    private const double GlyphInkScale = 0.8;

    private static readonly TextureCache Pictures = new(32);

    private readonly Gtk.Overlay _root;
    private readonly Gtk.Label _glyph;
    private readonly Adw.Bin _pictureFrame;
    private readonly Gtk.Picture _picture;
    private IAccountIconInfo? _account;
    private bool _isProfilePictureEnabled = true;
    private int _size;
    private int _glyphSize;

    public WinoAccountIcon(int size = 28)
    {
        _root = Gtk.Overlay.New();
        _root.AccessibleRole = Gtk.AccessibleRole.Img;
        _root.SetHalign(Gtk.Align.Center);
        _root.SetValign(Gtk.Align.Center);
        _root.SetHexpand(false);

        _glyphSize = GlyphSize(size);
        _glyph = WinoIcons.Label(string.Empty, _glyphSize);
        _root.SetChild(_glyph);

        _picture = Gtk.Picture.New();
        _picture.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _picture.SetContentFit(Gtk.ContentFit.Cover);
        _picture.SetCanShrink(true);
        _pictureFrame = Adw.Bin.New();
        _pictureFrame.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _pictureFrame.AddCssClass(WinoCssClass.Avatar);
        _pictureFrame.SetOverflow(Gtk.Overflow.Hidden);
        _pictureFrame.SetChild(_picture);
        _pictureFrame.SetVisible(false);
        _root.AddOverlay(_pictureFrame);

        _size = Math.Max(1, size);
        _root.SetSizeRequest(_size, _size);
    }

    public Gtk.Widget Root => _root;

    public int Size
    {
        get => _size;
        set
        {
            var size = Math.Max(1, value);
            if (_size == size) return;
            _size = size;
            _root.SetSizeRequest(size, size);
            _glyph.RemoveCssClass(WinoIcons.SizeClass(_glyphSize));
            _glyphSize = GlyphSize(size);
            _glyph.AddCssClass(WinoIcons.SizeClass(_glyphSize));
        }
    }

    /// <summary>When false the provider glyph is shown even if a picture exists.</summary>
    public bool IsProfilePictureEnabled
    {
        get => _isProfilePictureEnabled;
        set
        {
            if (_isProfilePictureEnabled == value) return;
            _isProfilePictureEnabled = value;
            Update();
        }
    }

    public IAccountIconInfo? Account
    {
        get => _account;
        set
        {
            _account = value;
            Update();
        }
    }

    /// <summary>The account name; the control does not know it.</summary>
    public string? AccessibleName
    {
        set => WinoA11y.Label(_root, value);
    }

    /// <summary>Drops the decoded picture of <paramref name="path"/> after the file was replaced.</summary>
    public static void ForgetPicture(string path) => Pictures.Remove(path);

    private void Update()
    {
        var account = _account;
        var tint = IsHexColor(account?.AccountColorHex) ? account?.AccountColorHex : null;
        var glyph = account is null ? string.Empty
            : string.IsNullOrEmpty(account.ProviderGlyph) ? WinoIcons.Glyph(WinoAccountIconGlyphs.ProviderGlyph(account.Provider, tint is not null))
            : account.ProviderGlyph;
        WinoIcons.SetGlyph(_glyph, glyph, tint);

        var texture = LoadPicture(_isProfilePictureEnabled ? account?.ProfilePicturePath : null);
        _picture.SetPaintable(texture);
        _pictureFrame.SetVisible(texture is not null);
        _glyph.SetVisible(texture is null);
    }

    // Account pictures are few and local, so the first use decodes synchronously; a missing file is not cached and is
    // retried on the next update.
    private static Gdk.Texture? LoadPicture(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Pictures.TryGet(path, out var cached) && cached is not null) return cached;
        var texture = Textures.FromFile(path);
        if (texture is not null) Pictures.Set(path, texture);
        return texture;
    }

    private static int GlyphSize(int size) => Math.Max(8, (int)Math.Round(size * GlyphInkScale));

    private static bool IsHexColor(string? value)
        => value is { Length: 7 } && value[0] == '#' && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;
}

/// <summary>The overlapped icons of merged accounts; later icons lie on top.</summary>
public sealed class WinoAccountIconStack
{
    private readonly Gtk.Overlay _root;
    private readonly Gtk.Box _spacer;
    private readonly int _size;
    private readonly int _maximum;
    private readonly List<Gtk.Widget> _icons = [];
    private IAccountIconInfo[] _accounts = [];

    public WinoAccountIconStack(int size = 28, int maximum = 3)
    {
        _size = Math.Max(1, size);
        _maximum = Math.Max(1, maximum);
        _root = Gtk.Overlay.New();
        _root.AccessibleRole = Gtk.AccessibleRole.Group;
        _root.SetHalign(Gtk.Align.Start);
        _root.SetValign(Gtk.Align.Center);
        _spacer = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _spacer.AccessibleRole = Gtk.AccessibleRole.Presentation;
        _spacer.SetSizeRequest(_size, _size);
        _root.SetChild(_spacer);
    }

    public Gtk.Widget Root => _root;

    public void SetAccounts(IAccountIconInfo[] accounts)
    {
        var shown = accounts.Length > _maximum ? accounts[.._maximum] : accounts;
        if (shown.AsSpan().SequenceEqual(_accounts)) return;
        _accounts = [.. shown];

        foreach (var icon in _icons) _root.RemoveOverlay(icon);
        _icons.Clear();

        // GTK margins cannot be negative, so the overlap is built from growing start margins.
        var step = (int)Math.Round(_size * 0.6);
        for (var index = 0; index < shown.Length; index++)
        {
            var icon = new WinoAccountIcon(_size) { Account = shown[index] };
            icon.Root.SetHalign(Gtk.Align.Start);
            icon.Root.SetValign(Gtk.Align.Center);
            icon.Root.SetMarginStart(index * step);
            _root.AddOverlay(icon.Root);
            _icons.Add(icon.Root);
        }
        _spacer.SetSizeRequest(_size + Math.Max(0, shown.Length - 1) * step, _size);
    }
}
