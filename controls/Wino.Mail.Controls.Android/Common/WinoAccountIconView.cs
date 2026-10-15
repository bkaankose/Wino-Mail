using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;
using Wino.Mail.Controls.Core.AccountIcon;
using Wino.Presentation.Controls;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Common;

/// <summary>
/// An account avatar with its provider (canvas Components, "Avatar with provider"; UIKit <c>WinoAccountIconView</c>):
/// the profile picture when enabled and present, otherwise the initials on the account colour, with the provider glyph
/// on a small white tile in the bottom-trailing corner. Usable from XML as <c>com.winomail.controls.WinoAccountIconView</c>.
/// </summary>
[Register("com.winomail.controls.WinoAccountIconView")]
public class WinoAccountIconView : FrameLayout
{
    private static readonly Dictionary<string, Bitmap?> Pictures = new(StringComparer.Ordinal);
    private WinoContactPicture? _picture;
    private WinoIconView? _badge;
    private IAccountIconInfo? _account;
    private string? _displayName;
    private double _sizeDp = 40;
    private bool _isProfilePictureEnabled = true;
    private bool _showsProvider = true;

    public WinoAccountIconView(Context context) : base(context) => Initialize(context);
    public WinoAccountIconView(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize(context);
    protected WinoAccountIconView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    private void Initialize(Context context)
    {
        SetClipChildren(false);
        _picture = new WinoContactPicture(context);
        AddView(_picture, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));
        _badge = new WinoIconView(context) { Colorful = false };
        var tile = new GradientDrawable();
        tile.SetColor(Color.White);
        tile.SetCornerRadius(WinoUnits.DpF(context, 4));
        tile.SetStroke(WinoUnits.Dp(context, 1), new Color(0, 0, 0, 26));
        _badge.Background = tile;
        _badge.SetTextColor(new Color(0x1B, 0x1B, 0x1B));
        AddView(_badge);
        ImportantForAccessibility = ImportantForAccessibility.No;
        Refresh();
    }

    public double Size
    {
        get => _sizeDp;
        set { _sizeDp = value; Refresh(); }
    }

    public bool IsProfilePictureEnabled
    {
        get => _isProfilePictureEnabled;
        set { _isProfilePictureEnabled = value; Refresh(); }
    }

    /// <summary>Shows the provider tile (off for the compact account switcher).</summary>
    public bool ShowsProvider
    {
        get => _showsProvider;
        set { _showsProvider = value; Refresh(); }
    }

    /// <summary>The account name the initials come from.</summary>
    public string? DisplayName
    {
        get => _displayName;
        set { _displayName = value; Refresh(); }
    }

    public IAccountIconInfo? Account
    {
        get => _account;
        set { _account = value; Refresh(); }
    }

    /// <summary>The shared provider glyph rule (Presentation.Core).</summary>
    public static global::Wino.Core.Domain.Enums.WinoIconGlyph ProviderGlyph(AccountIconProvider provider, bool isTinted)
        => WinoAccountIconGlyphs.ProviderGlyph(provider, isTinted);

    private void Refresh()
    {
        if (_picture is null || _badge is null || Context is not { } context) return;
        _picture.Size = _sizeDp;
        var color = WinoColors.FromHexString(_account?.AccountColorHex);
        _picture.SetIdentity(_displayName, _displayName, color);
        _picture.Image = _isProfilePictureEnabled ? LoadPicture(_account?.ProfilePicturePath) : null;

        double badge = Math.Round(_sizeDp * 0.45);
        _badge.Visibility = _showsProvider && _account is not null ? ViewStates.Visible : ViewStates.Gone;
        if (_account is { } account) _badge.Icon = ProviderGlyph(account.Provider, false);
        _badge.GlyphSize = badge * 0.66;
        int badgePx = WinoUnits.Dp(context, badge);
        int overhang = WinoUnits.Dp(context, 3);
        _badge.LayoutParameters = new LayoutParams(badgePx, badgePx, GravityFlags.End | GravityFlags.Bottom)
        {
            RightMargin = -overhang,
            BottomMargin = -overhang
        };
    }

    /// <summary>Decodes a profile picture once per path; a missing or unreadable file shows the initials.</summary>
    private static Bitmap? LoadPicture(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (Pictures)
        {
            if (Pictures.TryGetValue(path, out var cached)) return cached;
            Bitmap? bitmap = File.Exists(path) ? BitmapFactory.DecodeFile(path) : null;
            if (Pictures.Count > 64) Pictures.Clear();
            Pictures[path] = bitmap;
            return bitmap;
        }
    }
}
