using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Wino.Mail.Controls.Core;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Common;

/// <summary>
/// A round initials avatar or picture (UIKit <c>WinoContactPicture</c>): the picture centre-cropped into a circle, or
/// up to two initials on the stable Wino palette colour of the address (shared <see cref="ContactPictureAppearance"/>).
/// Usable from XML as <c>com.winomail.controls.WinoContactPicture</c>.
/// </summary>
[Register("com.winomail.controls.WinoContactPicture")]
public class WinoContactPicture : View
{
    private readonly Paint _fill = new(PaintFlags.AntiAlias);
    private readonly Paint _text = new(PaintFlags.AntiAlias);
    private readonly Paint _image = new(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
    private readonly RectF _bounds = new();
    private double _sizeDp = 40;
    private bool _isSquare;
    private string _initials = "?";
    private Bitmap? _bitmap;
    private Color _color = WinoColors.AvatarColor(null);

    public WinoContactPicture(Context context) : base(context) => Initialize();
    public WinoContactPicture(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize();
    protected WinoContactPicture(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    private void Initialize()
    {
        _text.Color = Color.White;
        _text.TextAlign = Paint.Align.Center;
        _text.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
        ImportantForAccessibility = ImportantForAccessibility.No;
    }

    /// <summary>Diameter in dp.</summary>
    public double Size
    {
        get => _sizeDp;
        set
        {
            if (_sizeDp == value) return;
            _sizeDp = value;
            RequestLayout();
            Invalidate();
        }
    }

    /// <summary>Rounded square (4 dp corners) instead of a circle, for account and calendar pictures.</summary>
    public bool IsSquare
    {
        get => _isSquare;
        set { _isSquare = value; Invalidate(); }
    }

    /// <summary>Initials from <paramref name="displayName"/>, colour from <paramref name="colorKey"/> unless overridden.</summary>
    public void SetIdentity(string? displayName, string? colorKey, Color? colorOverride = null)
    {
        _initials = ContactPictureAppearance.Initials(displayName);
        _color = colorOverride ?? WinoColors.AvatarColor(colorKey);
        Invalidate();
    }

    /// <summary>The picture; null shows the initials.</summary>
    public Bitmap? Image
    {
        get => _bitmap;
        set
        {
            if (ReferenceEquals(_bitmap, value)) return;
            _bitmap = value;
            _image.SetShader(null);
            Invalidate();
        }
    }

    public static string Initials(string? name) => ContactPictureAppearance.Initials(name);

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int size = WinoUnits.Dp(this.RequireContext(), _sizeDp);
        SetMeasuredDimension(ResolveSize(size, widthMeasureSpec), ResolveSize(size, heightMeasureSpec));
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float size = Math.Min(Width, Height);
        if (size <= 0) return;
        _bounds.Set((Width - size) / 2, (Height - size) / 2, (Width + size) / 2, (Height + size) / 2);
        float radius = _isSquare ? WinoUnits.DpF(this.RequireContext(), 4) : size / 2;

        if (_bitmap is { } bitmap && bitmap.Width > 0 && bitmap.Height > 0)
        {
            var shader = new BitmapShader(bitmap, WinoGraphics.Clamp, WinoGraphics.Clamp);
            float scale = Math.Max(size / bitmap.Width, size / bitmap.Height);
            var matrix = new Matrix();
            matrix.SetScale(scale, scale);
            matrix.PostTranslate(_bounds.Left + (size - bitmap.Width * scale) / 2, _bounds.Top + (size - bitmap.Height * scale) / 2);
            shader.SetLocalMatrix(matrix);
            _image.SetShader(shader);
            canvas.DrawRoundRect(_bounds, radius, radius, _image);
            return;
        }

        _fill.Color = _color;
        canvas.DrawRoundRect(_bounds, radius, radius, _fill);
        _text.TextSize = size * 0.375f;
        var metrics = _text.GetFontMetrics();
        float baseline = _bounds.CenterY() - ((metrics?.Ascent ?? 0) + (metrics?.Descent ?? 0)) / 2;
        canvas.DrawText(_initials, _bounds.CenterX(), baseline, _text);
    }
}
