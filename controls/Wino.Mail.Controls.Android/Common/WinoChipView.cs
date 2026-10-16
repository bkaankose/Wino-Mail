using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Chip;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Common;

/// <summary>
/// A small tile chip (UIKit <c>WinoChipView</c>): an optional glyph or dot and a short text on a rounded fill. Mail rows
/// use it for categories, intelligence tiles and account nicknames. Interactive chips (filters, recipients) are
/// Material <see cref="Chip"/>s from <see cref="WinoChips"/>.
/// </summary>
[Register("com.winomail.controls.WinoChipView")]
public class WinoChipView : LinearLayout
{
    private readonly GradientDrawable _fill = new();
    private TextView? _glyph;
    private View? _dot;
    private TextView? _text;
    private double _heightDp = 18;

    public WinoChipView(Context context) : base(context) => Initialize(context);
    public WinoChipView(Context context, IAttributeSet? attrs) : base(context, attrs) => Initialize(context);
    protected WinoChipView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    private void Initialize(Context context)
    {
        Orientation = Orientation.Horizontal;
        SetGravity(GravityFlags.CenterVertical);
        _fill.SetCornerRadius(WinoUnits.DpF(context, 4));
        _fill.SetColor(WinoColors.SubtleFill(context));
        Background = _fill;
        int horizontal = WinoUnits.Dp(context, 6);
        SetPadding(horizontal, 0, horizontal, 0);
        SetMinimumHeight(WinoUnits.Dp(context, _heightDp));

        _dot = new View(context) { Visibility = ViewStates.Gone };
        var dot = new GradientDrawable();
        dot.SetShape(ShapeType.Oval);
        _dot.Background = dot;
        int dotSize = WinoUnits.Dp(context, 6);
        AddView(_dot, new LayoutParams(dotSize, dotSize) { MarginEnd = WinoUnits.Dp(context, 4) });

        _glyph = new TextView(context) { Visibility = ViewStates.Gone, Typeface = WinoIcons.Mono(context) };
        _glyph.SetIncludeFontPadding(false);
        _glyph.SetTextSize(ComplexUnitType.Dip, 11);
        AddView(_glyph, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginEnd = WinoUnits.Dp(context, 4) });

        _text = new TextView(context);
        _text.SetIncludeFontPadding(false);
        _text.SetSingleLine(true);
        _text.Ellipsize = TextUtils.TruncateAt.End;
        _text.SetTextSize(ComplexUnitType.Sp, 11);
        _text.SetTextColor(WinoColors.PrimaryText(context));
        AddView(_text, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
    }

    public string Text
    {
        get => _text?.Text ?? string.Empty;
        set
        {
            if (_text is null) return;
            _text.Text = value;
            _text.Visibility = string.IsNullOrEmpty(value) ? ViewStates.Gone : ViewStates.Visible;
            if (_glyph is not null && _glyph.LayoutParameters is LayoutParams glyphLayout)
                glyphLayout.MarginEnd = string.IsNullOrEmpty(value) ? 0 : WinoUnits.Dp(this.RequireContext(), 4);
        }
    }

    /// <summary>A WinoIcons glyph string before the text; null hides it.</summary>
    public void SetGlyph(string? glyph, double sizeDp = 11)
    {
        if (_glyph is null) return;
        _glyph.Text = glyph ?? string.Empty;
        _glyph.SetTextSize(ComplexUnitType.Dip, (float)sizeDp);
        _glyph.Visibility = string.IsNullOrEmpty(glyph) ? ViewStates.Gone : ViewStates.Visible;
    }

    public void SetGlyph(WinoIconGlyph icon, double sizeDp = 11) => SetGlyph(icon == WinoIconGlyph.None ? null : WinoIcons.Glyph(icon), sizeDp);

    public Color Fill
    {
        set => _fill.SetColor(value);
    }

    public Color TextColor
    {
        set => _text?.SetTextColor(value);
    }

    public Color? GlyphTint
    {
        set
        {
            if (_glyph is not null && Context is { } context) _glyph.SetTextColor(value ?? WinoColors.SecondaryText(context));
        }
    }

    /// <summary>A coloured dot before the text (an intelligence tile without a glyph); null hides it.</summary>
    public Color? DotColor
    {
        set
        {
            if (_dot is null) return;
            _dot.Visibility = value is null ? ViewStates.Gone : ViewStates.Visible;
            if (value is { } color && _dot.Background is GradientDrawable dot) dot.SetColor(color);
        }
    }

    /// <summary>Chip height in dp (18 for row tiles).</summary>
    public double ChipHeight
    {
        get => _heightDp;
        set
        {
            _heightDp = value;
            SetMinimumHeight(WinoUnits.Dp(this.RequireContext(), value));
        }
    }

    /// <summary>Longest text before it ellipsizes, in dp; 0 for no limit.</summary>
    public double MaxTextWidth
    {
        set => _text?.SetMaxWidth(value > 0 ? WinoUnits.Dp(this.RequireContext(), value) : int.MaxValue);
    }
}

/// <summary>
/// Material chips with Wino glyphs (canvas Components "Chips, pills and badges"): the assist chip with a menu chevron
/// (filter pull-down), the checkable filter chip, the recipient input chip and the intelligence pill.
/// </summary>
public static class WinoChips
{
    /// <summary>An assist chip with a leading Wino glyph and a trailing chevron (opens a menu).</summary>
    public static Chip Assist(Context context, string text, WinoIconGlyph glyph = WinoIconGlyph.None)
    {
        var chip = Create(context, text, glyph);
        chip.CloseIconVisible = true;
        chip.SetCloseIconResource(Resource.Drawable.wino_ic_arrow_drop_down);
        chip.CloseIconTint = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.SecondaryText(context));
        chip.CloseIconSize = WinoTokens.PxF(context, Resource.Dimension.wino_m3_chip_icon);
        return chip;
    }

    /// <summary>
    /// A checkable Material 3 filter chip: the secondary container while checked, the outline otherwise. The canvas
    /// Material pass chip rows (Search, Contacts, To Do) draw no check mark, so the checked chip keeps its width.
    /// </summary>
    public static Chip Filter(Context context, string text, bool isChecked = false)
    {
        var chip = Create(context, text, WinoIconGlyph.None);
        chip.Checkable = true;
        chip.CheckedIconVisible = false;
        chip.Checked = isChecked;
        // Checked: the Material 3 secondary container without a stroke (canvas Material pass Search chips).
        int[][] states = [[global::Android.Resource.Attribute.StateChecked], []];
        chip.ChipBackgroundColor = new global::Android.Content.Res.ColorStateList(states, [WinoColors.SecondaryContainer(context).ToArgb(), global::Android.Graphics.Color.Transparent.ToArgb()]);
        chip.ChipStrokeColor = new global::Android.Content.Res.ColorStateList(states, [WinoColors.SecondaryContainer(context).ToArgb(), WinoColors.Outline(context).ToArgb()]);
        chip.SetTextColor(new global::Android.Content.Res.ColorStateList(states, [WinoColors.PrimaryText(context).ToArgb(), WinoColors.SecondaryText(context).ToArgb()]));
        return chip;
    }

    /// <summary>A recipient chip: initials avatar, the name and a remove (dismiss) button.</summary>
    public static Chip Input(Context context, string name, string? address)
    {
        var chip = Create(context, name, WinoIconGlyph.None);
        chip.ChipIcon = new InitialsDrawable(context, name, address);
        chip.ChipIconVisible = true;
        chip.CloseIconVisible = true;
        chip.CloseIcon = WinoIcons.Drawable(context, WinoIconGlyph.Dismiss, 10, WinoColors.SecondaryText(context));
        return chip;
    }

    /// <summary>The intelligence pill: a glyph and text in accent on an accent tint, not interactive.</summary>
    public static Chip Pill(Context context, string text, WinoIconGlyph glyph)
    {
        var accent = WinoColors.AccentText(context);
        var chip = Create(context, text, WinoIconGlyph.None);
        chip.ChipIcon = WinoIcons.Drawable(context, glyph, 12, accent);
        chip.ChipIconVisible = glyph != WinoIconGlyph.None;
        chip.ChipBackgroundColor = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.WithAlpha(WinoColors.Accent(context), 0.13));
        chip.ChipStrokeWidth = 0;
        chip.SetTextColor(accent);
        Corner(chip, context, 12);
        chip.Clickable = false;
        return chip;
    }

    private static Chip Create(Context context, string text, WinoIconGlyph glyph)
    {
        var chip = new Chip(context) { Text = text };
        chip.ShapeAppearanceModel = chip.ShapeAppearanceModel.WithCornerSize(WinoTokens.PxF(context, Resource.Dimension.wino_m3_chip_radius));
        chip.ChipMinHeight = WinoTokens.PxF(context, Resource.Dimension.wino_m3_chip_height);
        chip.ChipStrokeWidth = WinoTokens.PxF(context, Resource.Dimension.wino_m3_stroke);
        chip.ChipStrokeColor = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.Outline(context));
        chip.ChipBackgroundColor = global::Android.Content.Res.ColorStateList.ValueOf(global::Android.Graphics.Color.Transparent);
        chip.SetTextAppearanceResource(Resource.Style.TextAppearance_WinoKit_Label);
        chip.SetTextColor(WinoColors.SecondaryText(context));
        chip.SetEnsureMinTouchTargetSize(false);
        if (glyph != WinoIconGlyph.None)
        {
            chip.ChipIcon = WinoIcons.Drawable(context, glyph, 14, WinoColors.PrimaryText(context));
            chip.ChipIconVisible = true;
        }
        return chip;
    }

    private static void Corner(Chip chip, Context context, double radiusDp)
        => chip.ShapeAppearanceModel = chip.ShapeAppearanceModel.WithCornerSize(WinoUnits.DpF(context, radiusDp));

    /// <summary>A 20 dp initials circle for recipient chips.</summary>
    private sealed class InitialsDrawable : Drawable
    {
        private readonly Paint _fill = new(PaintFlags.AntiAlias);
        private readonly Paint _text = new(PaintFlags.AntiAlias);
        private readonly string _initials;
        private readonly int _size;

        public InitialsDrawable(Context context, string? name, string? address)
        {
            _initials = WinoContactPicture.Initials(string.IsNullOrWhiteSpace(name) ? address : name);
            _fill.Color = WinoColors.AvatarColor(string.IsNullOrWhiteSpace(address) ? name : address);
            _text.Color = Color.White;
            _text.TextAlign = Paint.Align.Center;
            _text.SetTypeface(Typeface.Create("sans-serif-medium", TypefaceStyle.Normal));
            _size = WinoUnits.Dp(context, 20);
            _text.TextSize = _size * 0.45f;
        }

        public override int IntrinsicWidth => _size;
        public override int IntrinsicHeight => _size;
        public override int Opacity => (int)Format.Translucent;

        public override void Draw(Canvas canvas)
        {
            var bounds = Bounds;
            float radius = Math.Min(bounds.Width(), bounds.Height()) / 2f;
            canvas.DrawCircle(bounds.ExactCenterX(), bounds.ExactCenterY(), radius, _fill);
            var metrics = _text.GetFontMetrics();
            canvas.DrawText(_initials, bounds.ExactCenterX(), bounds.ExactCenterY() - ((metrics?.Ascent ?? 0) + (metrics?.Descent ?? 0)) / 2, _text);
        }

        public override void SetAlpha(int alpha) => _fill.Alpha = alpha;
        public override void SetColorFilter(ColorFilter? colorFilter) { }
    }
}
