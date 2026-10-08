using AppKit;
using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Common;

/// <summary>
/// The Mac twin of the Windows WinoAccountIcon: the account's profile picture in a circle when
/// one is stored, otherwise its provider glyph (multi-colour Microsoft / Google, or the mono twin
/// tinted with the account colour). Shell rows use 28pt, compact rows 22pt.
/// </summary>
public sealed class WinoAccountIconView : NSView
{
    /// <summary>Provider glyphs draw inside the em box; this inset matches a picture's drawn size (Windows uses 0.8).</summary>
    private const double GlyphInkScale = 0.8;

    private readonly NSImageView _picture;
    private readonly WinoIconView _glyph;
    private readonly NSLayoutConstraint _width, _height;
    private IAccountIconInfo? _account;
    private string? _loadedPath;
    private double _size;

    public WinoAccountIconView(double size = 28)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        _picture = new NSImageView { ImageScaling = NSImageScale.ProportionallyUpOrDown, TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true, WantsLayer = true };
        _glyph = new WinoIconView();
        WinoLayout.Fill(_picture, this);
        AddSubview(_glyph);
        NSLayoutConstraint.ActivateConstraints(
        [
            _glyph.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _glyph.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _glyph.WidthAnchor.ConstraintEqualTo(WidthAnchor),
            _glyph.HeightAnchor.ConstraintEqualTo(HeightAnchor)
        ]);
        _width = WidthAnchor.ConstraintEqualTo((nfloat)size);
        _height = HeightAnchor.ConstraintEqualTo((nfloat)size);
        _width.Active = _height.Active = true;
        SetContentHuggingPriorityForOrientation(1000, NSLayoutConstraintOrientation.Horizontal);
        SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);
        Size = size;
    }

    public double Size
    {
        get => _size;
        set
        {
            _size = value;
            _width.Constant = _height.Constant = (nfloat)value;
            _glyph.PointSize = value * GlyphInkScale;
            if (_picture.Layer is { } layer) { layer.CornerRadius = (nfloat)(value / 2); layer.MasksToBounds = true; }
        }
    }

    /// <summary>When false the provider glyph is shown even if a picture exists (Windows "show profile pictures" off).</summary>
    public bool IsProfilePictureEnabled { get; set { field = value; Update(); } } = true;

    public IAccountIconInfo? Account
    {
        get => _account;
        set { _account = value; Update(); }
    }

    private void Update()
    {
        var account = _account;
        var tint = WinoStyle.FromHexString(account?.AccountColorHex);
        _glyph.Glyph = account is null ? string.Empty : WinoIcons.Glyph(ProviderGlyph(account.Provider, tint is not null));
        _glyph.Tint = tint;

        var path = IsProfilePictureEnabled ? account?.ProfilePicturePath : null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _loadedPath = null;
            _picture.Image = null;
            _picture.Hidden = true;
            _glyph.Hidden = false;
            return;
        }

        if (_loadedPath != path)
        {
            _loadedPath = path;
            _picture.Image = new NSImage(path);
        }
        _picture.Hidden = _picture.Image is null;
        _glyph.Hidden = !_picture.Hidden;
    }

    /// <summary>Same mapping as Windows AccountIconGlyphs: a tinted icon uses the mono twins of the brand logos.</summary>
    public static WinoIconGlyph ProviderGlyph(AccountIconProvider provider, bool isTinted) => provider switch
    {
        AccountIconProvider.Microsoft => isTinted ? WinoIconGlyph.MicrosoftMono : WinoIconGlyph.Microsoft,
        AccountIconProvider.Google => isTinted ? WinoIconGlyph.GoogleMono : WinoIconGlyph.Google,
        AccountIconProvider.ICloud => WinoIconGlyph.Apple,
        AccountIconProvider.Yahoo => WinoIconGlyph.Yahoo,
        _ => WinoIconGlyph.IMAP,
    };

    public override CGSize IntrinsicContentSize => new((nfloat)_size, (nfloat)_size);
}
