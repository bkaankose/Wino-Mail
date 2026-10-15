using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Text;
using Android.Text.Style;
using Android.Util;
using Android.Views;
using Android.Widget;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Presentation.Android;

/// <summary>
/// The WinoIcons fonts (icons/README.md) from the app assets, which this library ships as Fonts/*.ttf: the mono font
/// and the colorful light and dark fonts with the same code points as Windows and the Apple heads. Glyphs come from
/// the shared <see cref="WinoIconGlyphs"/> map. Android twin of the UIKit <c>WinoIcons</c>.
/// </summary>
public static class WinoIcons
{
    private static Typeface? _mono, _colorLight, _colorDark;
    private static WinoIconStyle _style = WinoIconStyle.Monochrome;

    /// <summary>Raised after <see cref="Style"/> changes so long-lived views can redraw.</summary>
    public static event EventHandler? StyleChanged;

    /// <summary>The user's icon style (Settings, Personalization); colorful glyphs follow it unless a view forces one.</summary>
    public static WinoIconStyle Style
    {
        get => _style;
        set
        {
            if (_style == value) return;
            _style = value;
            StyleChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static bool IsColorful => _style == WinoIconStyle.Colorful;

    public static Typeface Mono(Context context) => _mono ??= Load(context, "Fonts/WinoIcons.ttf");

    /// <summary>The colorful font for the appearance of <paramref name="context"/>.</summary>
    public static Typeface Colorful(Context context)
        => WinoColors.IsDark(context)
            ? _colorDark ??= Load(context, "Fonts/WinoIconsColor-Dark.ttf")
            : _colorLight ??= Load(context, "Fonts/WinoIconsColor-Light.ttf");

    /// <summary>The font for <paramref name="colorful"/>, or for the current <see cref="Style"/> when null.</summary>
    public static Typeface Typeface(Context context, bool? colorful = null) => colorful ?? IsColorful ? Colorful(context) : Mono(context);

    public static string Glyph(WinoIconGlyph icon) => icon == WinoIconGlyph.None ? string.Empty : WinoIconGlyphs.GetGlyph(icon);

    /// <summary>Shows <paramref name="icon"/> in <paramref name="view"/> at <paramref name="sizeDp"/>.</summary>
    public static void Apply(TextView view, WinoIconGlyph icon, double sizeDp = 16, bool? colorful = false)
    {
        var context = view.Context ?? throw new InvalidOperationException("The view has no context.");
        view.Typeface = Typeface(context, colorful);
        view.SetTextSize(ComplexUnitType.Dip, (float)sizeDp);
        view.SetIncludeFontPadding(false);
        view.Text = Glyph(icon);
        view.ImportantForAccessibility = string.IsNullOrEmpty(view.ContentDescription) ? ImportantForAccessibility.No : ImportantForAccessibility.Yes;
    }

    /// <summary>"glyph  text" with the glyph drawn in <paramref name="typeface"/>, for buttons that carry a Wino icon.</summary>
    public static Java.Lang.ICharSequence WithGlyph(WinoIconGlyph icon, string text, Typeface typeface)
    {
        var glyph = Glyph(icon);
        var builder = new SpannableStringBuilder(glyph + " " + text);
        builder.SetSpan(new TypefaceGlyphSpan(typeface), 0, glyph.Length, SpanTypes.ExclusiveExclusive);
        return builder;
    }

    /// <summary>A drawable of <paramref name="icon"/> for MaterialButton icons, chips, menus and toolbars (tinted by the host).</summary>
    public static Drawable Drawable(Context context, WinoIconGlyph icon, double sizeDp = 20, Color? tint = null, bool colorful = false)
        => new WinoGlyphDrawable(context, Glyph(icon), sizeDp, tint ?? WinoColors.PrimaryText(context), colorful);

    private static Typeface Load(Context context, string asset)
    {
        var assets = context.Assets ?? throw new InvalidOperationException("The app assets are unavailable.");
        return global::Android.Graphics.Typeface.CreateFromAsset(assets, asset)
            ?? global::Android.Graphics.Typeface.Default
            ?? throw new InvalidOperationException($"{asset} could not be loaded.");
    }

    /// <summary>Draws a span in another typeface (TypefaceSpan(Typeface) needs API 28).</summary>
    private sealed class TypefaceGlyphSpan(Typeface typeface) : MetricAffectingSpan
    {
        public override void UpdateDrawState(TextPaint? paint) => Apply(paint);
        public override void UpdateMeasureState(TextPaint paint) => Apply(paint);
        private void Apply(TextPaint? paint)
        {
            if (paint is not null) paint.SetTypeface(typeface);
        }
    }
}

/// <summary>One WinoIcons glyph as a square drawable, centred on its bounds. Tintable like a vector icon.</summary>
public sealed class WinoGlyphDrawable : Drawable
{
    private readonly Paint _paint = new(PaintFlags.AntiAlias);
    private readonly string _glyph;
    private readonly int _size;
    private readonly bool _colorful;

    public WinoGlyphDrawable(Context context, string glyph, double sizeDp, Color tint, bool colorful = false)
    {
        _glyph = glyph;
        _colorful = colorful;
        _size = WinoUnits.Dp(context, sizeDp);
        _paint.SetTypeface(colorful ? WinoIcons.Colorful(context) : WinoIcons.Mono(context));
        _paint.TextSize = _size;
        _paint.TextAlign = Paint.Align.Center;
        _paint.Color = tint;
    }

    public override int IntrinsicWidth => _size;
    public override int IntrinsicHeight => _size;

    public override int Opacity => (int)Format.Translucent;

    public override void Draw(Canvas canvas)
    {
        var bounds = Bounds;
        var metrics = _paint.GetFontMetrics();
        float baseline = bounds.ExactCenterY() - ((metrics?.Ascent ?? 0) + (metrics?.Descent ?? 0)) / 2;
        canvas.DrawText(_glyph, bounds.ExactCenterX(), baseline, _paint);
    }

    public override void SetAlpha(int alpha) => _paint.Alpha = alpha;

    public override void SetColorFilter(ColorFilter? colorFilter)
    {
        // Colorful glyphs keep their own palette; mono glyphs follow the host's tint (MaterialButton iconTint).
        if (!_colorful) _paint.SetColorFilter(colorFilter);
    }

    public override void SetTintList(ColorStateList? tint)
    {
        if (_colorful || tint is null) return;
        _paint.Color = new Color(tint.GetColorForState(GetState(), new Color(tint.DefaultColor)));
        InvalidateSelf();
    }

    public override bool IsStateful => false;
}

/// <summary>
/// A TextView that shows one WinoIcons glyph (UIKit <c>WinoIconView</c>). Usable from XML as
/// <c>com.winomail.presentation.WinoIconView</c>; follows the colorful style unless <see cref="Colorful"/> is set.
/// </summary>
[Register("com.winomail.presentation.WinoIconView")]
public class WinoIconView : TextView
{
    private WinoIconGlyph _icon;
    private double _sizeDp = 16;
    private bool? _colorful = false;

    public WinoIconView(Context context) : base(context) => Initialize();
    public WinoIconView(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize();
    public WinoIconView(Context context, IAttributeSet? attrs, int defStyleAttr) : base(context, attrs, defStyleAttr) => Initialize();
    protected WinoIconView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    private void Initialize()
    {
        Gravity = GravityFlags.Center;
        SetIncludeFontPadding(false);
        SetTextColor(WinoColors.SecondaryText(Context ?? throw new InvalidOperationException("The view has no context.")));
        // A glyph alone is decoration (its private-use character means nothing to TalkBack); a clickable glyph is a
        // button and is read by its content description (SetOnClickListener below).
        ImportantForAccessibility = ImportantForAccessibility.No;
        Refresh();
    }

    public override void SetOnClickListener(IOnClickListener? listener)
    {
        base.SetOnClickListener(listener);
        ImportantForAccessibility = listener is null ? ImportantForAccessibility.No : ImportantForAccessibility.Yes;
    }

    public override void OnInitializeAccessibilityNodeInfo(global::Android.Views.Accessibility.AccessibilityNodeInfo? info)
    {
        base.OnInitializeAccessibilityNodeInfo(info);
        if (info is null) return;
        // Announce the content description and the button role, never the glyph character.
        info.Text = null;
        if (Clickable) info.ClassName = "android.widget.Button";
    }

    public WinoIconGlyph Icon
    {
        get => _icon;
        set { _icon = value; Refresh(); }
    }

    /// <summary>Glyph size in dp.</summary>
    public double GlyphSize
    {
        get => _sizeDp;
        set { _sizeDp = value; Refresh(); }
    }

    /// <summary>True for the colorful font, false for mono, null to follow <see cref="WinoIcons.Style"/>.</summary>
    public bool? Colorful
    {
        get => _colorful;
        set { _colorful = value; Refresh(); }
    }

    private void Refresh()
    {
        if (Context is not { } context) return;
        Typeface = WinoIcons.Typeface(context, _colorful);
        SetTextSize(ComplexUnitType.Dip, (float)_sizeDp);
        Text = WinoIcons.Glyph(_icon);
    }
}
