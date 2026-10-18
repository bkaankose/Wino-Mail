using System.Globalization;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using Wino.Core.ViewModels;
using Google.Android.Material.Dialog;
using Google.Android.Material.Slider;
using Google.Android.Material.TextField;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings.Personalization;

/// <summary>
/// A small picture of a theme (iOS ThemeThumbnailView): its wallpaper or custom preview (centre crop), its gradient, or
/// the zone colour for the system theme, with radius 5 and a hairline stroke.
/// </summary>
public sealed class ThemeThumbnailView : ImageView
{
    private readonly int _width;
    private readonly int _height;

    /// <param name="width">The width dimension token.</param>
    /// <param name="height">The height dimension token.</param>
    public ThemeThumbnailView(Context context, int width, int height) : base(context)
    {
        _width = WinoTokens.Px(context, width);
        _height = WinoTokens.Px(context, height);
        SetScaleType(ScaleType.CenterCrop);
        ImportantForAccessibility = ImportantForAccessibility.No;
        LayoutParameters = new ViewGroup.LayoutParams(_width, _height);
        var outline = new GradientDrawable();
        outline.SetCornerRadius(WinoTokens.PxF(context, Resource.Dimension.wino_radius_medium));
        outline.SetColor(WinoColors.ZoneFill(context));
        outline.SetStroke(WinoTokens.Px(context, Resource.Dimension.wino_stroke_hairline), WinoColors.CardStroke(context));
        Background = outline;
        ClipToOutline = true;
    }

    /// <summary>Always its fixed size, whatever layout parameters the parent gives it.</summary>
    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) => SetMeasuredDimension(_width, _height);

    public void Show(AppThemeBase? theme) => SetImageBitmap(ImageFor(Context, theme));

    /// <summary>The picture of a theme for tiles and thumbnails: a custom theme's stored preview, a gradient or a wallpaper.</summary>
    public static Bitmap? ImageFor(Context? context, AppThemeBase? theme)
    {
        if (theme is null) return null;
        if (theme.AppThemeType == AppThemeType.Custom)
            return string.IsNullOrEmpty(theme.PreviewImage) || !File.Exists(theme.PreviewImage) ? null : BitmapFactory.DecodeFile(theme.PreviewImage);
        if (WinoThemeCatalog.GradientFor(theme.ThemeName) is { } gradient)
            return AndroidThemeService.GradientBitmap(gradient, context is not null && WinoColors.IsDark(context));
        // The system theme has no backdrop; its tile shows the plain surface.
        if (theme.AppThemeType == AppThemeType.System) return null;
        return theme.ThemeName is { } name ? AndroidThemeService.LoadAsset(name) : null;
    }
}

/// <summary>
/// A theme card in the gallery grid (canvas ThemeGallery; Windows theme tile, iOS WinoThemeTile): the picture filling a
/// 120 dp card with 12 dp corners, the name and the compatibility line over it at the foot, and while selected a 2 dp
/// primary stroke with a check badge in the top corner. Custom themes add Edit under the card.
/// </summary>
public sealed class ThemeTileView : LinearLayout
{
    private readonly FrameLayout _frame;
    private readonly GradientDrawable _ring = new();
    private readonly View _badge;

    public ThemeTileView(Context context, AppThemeBase theme, string? detail) : base(context)
    {
        Orientation = Orientation.Vertical;
        float radius = WinoTokens.PxF(context, Resource.Dimension.wino_m3_theme_card_radius);
        var shape = new GradientDrawable();
        shape.SetCornerRadius(radius);
        shape.SetColor(WinoColors.ZoneFill(context));
        _frame = new FrameLayout(context) { Clickable = true, Focusable = true, Background = shape, ClipToOutline = true };
        _frame.ContentDescription = string.IsNullOrWhiteSpace(detail) ? theme.ThemeName : $"{theme.ThemeName}, {detail}";

        var picture = new ImageView(context) { ImportantForAccessibility = ImportantForAccessibility.No };
        picture.SetScaleType(ImageView.ScaleType.CenterCrop);
        var image = ThemeThumbnailView.ImageFor(context, theme);
        picture.SetImageBitmap(image);
        _frame.AddView(picture, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var texts = new LinearLayout(context) { Orientation = Orientation.Vertical, ImportantForAccessibility = ImportantForAccessibility.NoHideDescendants };
        WinoTokens.Pad(texts, Resource.Dimension.wino_m3_theme_card_padding_horizontal, Resource.Dimension.wino_m3_theme_card_padding_vertical,
            Resource.Dimension.wino_m3_theme_card_padding_horizontal, Resource.Dimension.wino_m3_theme_card_padding_vertical);
        // Without a picture (the system theme) the labels sit on the plain card in the regular text colour.
        texts.AddView(Label(context, theme.ThemeName ?? string.Empty, WinoTextStyle.CaptionStrong, image is not null));
        if (!string.IsNullOrWhiteSpace(detail)) texts.AddView(Label(context, detail, WinoTextStyle.Tiny, image is not null));
        _frame.AddView(texts, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom | GravityFlags.Start));

        var badgeFill = new GradientDrawable();
        badgeFill.SetShape(ShapeType.Oval);
        badgeFill.SetColor(WinoColors.Accent(context));
        var badge = new ImageView(context) { Background = badgeFill, ImportantForAccessibility = ImportantForAccessibility.No, Visibility = ViewStates.Gone };
        badge.SetImageResource(Resource.Drawable.wino_ic_check);
        badge.ImageTintList = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.OnAccent(context));
        int inset = (WinoTokens.Px(context, Resource.Dimension.wino_m3_theme_card_badge) - WinoTokens.Px(context, Resource.Dimension.wino_m3_theme_card_badge_glyph)) / 2;
        badge.SetPadding(inset, inset, inset, inset);
        int badgeSize = WinoTokens.Px(context, Resource.Dimension.wino_m3_theme_card_badge);
        var badgeLayout = new FrameLayout.LayoutParams(badgeSize, badgeSize, GravityFlags.Top | GravityFlags.End);
        badgeLayout.SetMargins(0, WinoTokens.Px(context, Resource.Dimension.wino_m3_theme_card_badge_margin), WinoTokens.Px(context, Resource.Dimension.wino_m3_theme_card_badge_margin), 0);
        _frame.AddView(badge, badgeLayout);
        _badge = badge;

        _ring.SetCornerRadius(radius);
        _ring.SetColor(Color.Transparent);
        _frame.Foreground = _ring;
        AddView(_frame, new LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(context, Resource.Dimension.wino_m3_theme_card_height)));
    }

    private static TextView Label(Context context, string text, WinoTextStyle style, bool onImage)
    {
        var label = WinoTokens.Style(new TextView(context) { Text = text }, style);
        label.SetMaxLines(1);
        label.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        if (!onImage) return label;
        label.SetTextColor(WinoTokens.Color(context, Resource.Color.wino_on_image_text));
        label.SetShadowLayer(WinoTokens.PxF(context, Resource.Dimension.wino_text_shadow_radius), 0, WinoTokens.PxF(context, Resource.Dimension.wino_text_shadow_offset),
            WinoTokens.Color(context, Resource.Color.wino_on_image_shadow));
        return label;
    }

    public View Picture => _frame;

    public bool IsSelected
    {
        set
        {
            var context = this.RequireContext();
            _ring.SetStroke(value ? WinoTokens.Px(context, Resource.Dimension.wino_m3_theme_card_stroke) : 0, WinoColors.Accent(context));
            _badge.Visibility = value ? ViewStates.Visible : ViewStates.Gone;
            _frame.Selected = value;
        }
    }
}

/// <summary>
/// The theme card grid (canvas ThemeGallery): as many columns as cards of at least 104 dp fit, 12 dp apart, the cards
/// sharing the width; as tall as its rows.
/// </summary>
public sealed class ThemeGridLayout : ViewGroup
{
    public ThemeGridLayout(Context context) : base(context) { }

    private int Gap => WinoTokens.Px(this.RequireContext(), Resource.Dimension.wino_m3_theme_card_gap);

    private int Columns(int width)
    {
        int minimum = WinoTokens.Px(this.RequireContext(), Resource.Dimension.wino_m3_theme_card_min_width);
        return Math.Max(1, (width + Gap) / (minimum + Gap));
    }

    private int Cell(int width, int columns) => Math.Max(0, (width - (columns - 1) * Gap) / columns);

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int width = MeasureSpec.GetSize(widthMeasureSpec);
        int columns = Columns(width);
        int cell = Cell(width, columns);
        int rowHeight = 0, height = 0, column = 0;
        for (int index = 0; index < ChildCount; index++)
        {
            var child = GetChildAt(index);
            if (child is null || child.Visibility == ViewStates.Gone) continue;
            child.Measure(MeasureSpec.MakeMeasureSpec(cell, MeasureSpecMode.Exactly), MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified));
            rowHeight = Math.Max(rowHeight, child.MeasuredHeight);
            if (++column == columns)
            {
                height += rowHeight + Gap;
                rowHeight = 0;
                column = 0;
            }
        }
        if (column > 0) height += rowHeight;
        else if (height > 0) height -= Gap;
        SetMeasuredDimension(width, height + PaddingTop + PaddingBottom);
    }

    protected override void OnLayout(bool changed, int l, int t, int r, int b)
    {
        int width = r - l;
        int columns = Columns(width);
        int cell = Cell(width, columns);
        bool rtl = LayoutDirection == global::Android.Views.LayoutDirection.Rtl;
        int y = PaddingTop, column = 0, rowHeight = 0;
        for (int index = 0; index < ChildCount; index++)
        {
            var child = GetChildAt(index);
            if (child is null || child.Visibility == ViewStates.Gone) continue;
            int x = column * (cell + Gap);
            if (rtl) x = width - x - cell;
            child.Layout(x, y, x + cell, y + child.MeasuredHeight);
            rowHeight = Math.Max(rowHeight, child.MeasuredHeight);
            if (++column == columns)
            {
                column = 0;
                y += rowHeight + Gap;
                rowHeight = 0;
            }
        }
    }
}

/// <summary>
/// A colour well for the theme editor and the category form (iOS ThemeColorWell over UIColorWell): a 40 dp swatch over a
/// checkerboard that opens <see cref="WinoColorPicker"/>; raises <see cref="ColorPicked"/> with #RRGGBB, or #AARRGGBB
/// when alpha is allowed.
/// </summary>
public sealed class ThemeColorWell : FrameLayout
{
    private readonly bool _allowsAlpha;
    private readonly string _title;
    private readonly CheckerSwatch _swatch;
    private string? _hex;

    public ThemeColorWell(Context context, bool allowsAlpha, string accessibilityLabel) : base(context)
    {
        _allowsAlpha = allowsAlpha;
        _title = accessibilityLabel;
        ContentDescription = accessibilityLabel;
        Clickable = true;
        Focusable = true;
        _swatch = new CheckerSwatch(context);
        AddView(_swatch, new LayoutParams(WinoTokens.Px(context, Resource.Dimension.wino_size_control), WinoTokens.Px(context, Resource.Dimension.wino_size_control)));
        Click += (_, _) => WinoColorPicker.Show(context, _title, _hex, _allowsAlpha, hex =>
        {
            SetHex(hex);
            ColorPicked?.Invoke(this, hex);
        });
    }

    /// <summary>Raised with the hex value the user chose.</summary>
    public event EventHandler<string>? ColorPicked;

    /// <summary>Shows <paramref name="hex"/> without raising <see cref="ColorPicked"/>.</summary>
    public void SetHex(string? hex)
    {
        _hex = hex;
        _swatch.Color = WinoColors.FromHexString(hex);
    }

    /// <summary>Kept for parity with iOS (its well debounces); the Android picker commits on OK.</summary>
    public void Flush() { }
}

/// <summary>A colour swatch over a checkerboard, so translucent colours read as translucent (iOS CheckerSwatchView).</summary>
public sealed class CheckerSwatch : View
{
    private readonly Paint _paint = new(PaintFlags.AntiAlias);
    private Color? _color;

    public CheckerSwatch(Context context) : base(context) => ImportantForAccessibility = ImportantForAccessibility.No;

    public Color? Color
    {
        get => _color;
        set { _color = value; Invalidate(); }
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        var context = this.RequireContext();
        float radius = WinoTokens.PxF(context, Resource.Dimension.wino_radius_small);
        using var clip = new global::Android.Graphics.Path();
        clip.AddRoundRect(0, 0, Width, Height, radius, radius, global::Android.Graphics.Path.Direction.Cw!);
        canvas.Save();
        canvas.ClipPath(clip);
        canvas.DrawColor(WinoTokens.Color(context, Resource.Color.wino_checker_light));
        _paint.Color = WinoTokens.Color(context, Resource.Color.wino_checker_dark);
        float cell = WinoTokens.PxF(context, Resource.Dimension.wino_checker_cell);
        for (float y = 0; y < Height; y += cell)
            for (float x = ((int)(y / cell) % 2) * cell; x < Width; x += cell * 2)
                canvas.DrawRect(x, y, x + cell, y + cell, _paint);
        if (_color is { } color) canvas.DrawColor(color);
        canvas.Restore();
        _paint.Color = WinoColors.CardStroke(context);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = WinoTokens.PxF(context, Resource.Dimension.wino_stroke_hairline);
        canvas.DrawRoundRect(0.5f, 0.5f, Width - 0.5f, Height - 0.5f, radius, radius, _paint);
        _paint.SetStyle(Paint.Style.Fill);
    }
}

/// <summary>
/// The colour picker dialog (Android has no system colour picker; iOS UIColorPickerViewController): a preview swatch,
/// the Wino palette, red, green, blue (and alpha) sliders and the hex field, with Cancel and OK.
/// </summary>
public static class WinoColorPicker
{
    private static readonly string[] Palette =
    [
        "#0078D7", "#0984E3", "#4A69BD", "#6C5CE7", "#8C7AE6", "#881798", "#C239B3", "#E84393", "#E3008C", "#E81123",
        "#D63031", "#CA4F07", "#E17055", "#E58E26", "#E1B12C", "#05C46B", "#10893E", "#16A085", "#00838C", "#00A8B5",
        "#00CEC9", "#0097E6", "#5E81AC", "#6B7B8C", "#767676", "#FFFFFF", "#F3F3F3", "#202020", "#000000"
    ];

    public static void Show(Context context, string title, string? hex, bool allowsAlpha, Action<string> picked)
    {
        var current = WinoColors.FromHexString(hex) ?? WinoTokens.Color(context, Resource.Color.wino_default_accent);
        int a = current.A, r = current.R, g = current.G, b = current.B;
        var body = new LinearLayout(context) { Orientation = Orientation.Vertical };
        int pad = WinoTokens.Px(context, Resource.Dimension.wino_space_2xl);
        body.SetPadding(pad, WinoTokens.Px(context, Resource.Dimension.wino_space_s), pad, 0);

        var preview = new CheckerSwatch(context);
        body.AddView(preview, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(context, Resource.Dimension.wino_size_control)));

        var swatches = new GridLayout(context) { ColumnCount = 10 };
        int size = WinoTokens.Px(context, Resource.Dimension.wino_size_swatch_small);
        int gap = WinoTokens.Px(context, Resource.Dimension.wino_space_3xs);
        var hexLayout = SettingsBinder.CreateTextField(context, allowsAlpha ? Translator.ApplicationThemeEditor_HexFormatWithAlpha : Translator.ApplicationThemeEditor_HexFormat);
        var field = hexLayout.EditText;
        var sliders = new List<(Slider Slider, Func<int> Read, Action<int> Write)>();
        bool updating = false;

        void Apply()
        {
            updating = true;
            try
            {
                var color = new Color(r, g, b, allowsAlpha ? a : 255);
                preview.Color = color;
                foreach (var (slider, read, _) in sliders) slider.Value = read();
                var text = Format(color, allowsAlpha);
                if (field is not null && field.Text != text) field.Text = text;
            }
            finally { updating = false; }
        }

        foreach (var value in Palette)
        {
            if (WinoColors.FromHexString(value) is not { } color) continue;
            var swatch = new CheckerSwatch(context) { Color = color, Clickable = true, ContentDescription = value };
            swatch.Click += (_, _) => { r = color.R; g = color.G; b = color.B; a = 255; Apply(); };
            var layout = new GridLayout.LayoutParams { Width = size, Height = size };
            layout.SetMargins(gap, gap, gap, gap);
            swatches.AddView(swatch, layout);
        }
        body.AddView(swatches, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { TopMargin = WinoTokens.Px(context, Resource.Dimension.wino_space_m) });

        void AddSlider(string label, Func<int> read, Action<int> write)
        {
            var slider = new Slider(context) { ValueFrom = 0, ValueTo = 255, StepSize = 1, ContentDescription = label, Value = read() };
            WinoMaterialAccent.Tint(slider);
            slider.AddOnChangeListener(new SliderChanged((value, fromUser) =>
            {
                if (updating || !fromUser) return;
                write((int)value);
                Apply();
            }));
            var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            var caption = new TextView(context) { Text = label };
            caption.SetTextColor(WinoColors.SecondaryText(context));
            row.AddView(caption, new LinearLayout.LayoutParams(WinoTokens.Px(context, Resource.Dimension.wino_space_xl), ViewGroup.LayoutParams.WrapContent));
            row.AddView(slider, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
            body.AddView(row);
            sliders.Add((slider, read, write));
        }

        AddSlider("R", () => r, value => r = value);
        AddSlider("G", () => g, value => g = value);
        AddSlider("B", () => b, value => b = value);
        if (allowsAlpha) AddSlider("A", () => a, value => a = value);
        body.AddView(hexLayout);
        if (field is not null)
        {
            field.TextChanged += (_, _) =>
            {
                if (updating) return;
                var text = (field.Text ?? string.Empty).Trim();
                if (text.Length > 0 && !text.StartsWith('#')) text = "#" + text;
                if (text.Length is 7 or 9 && WinoColors.FromHexString(text) is { } typed)
                {
                    r = typed.R; g = typed.G; b = typed.B; a = typed.A;
                    updating = true;
                    try
                    {
                        preview.Color = new Color(r, g, b, allowsAlpha ? a : 255);
                        foreach (var (slider, read, _) in sliders) slider.Value = read();
                    }
                    finally { updating = false; }
                }
            };
        }
        Apply();

        var scroll = new ScrollView(context);
        scroll.AddView(body);
        new MaterialAlertDialogBuilder(context)
            .SetTitle(title)
            .SetView(scroll)
            .SetNegativeButton(Translator.Buttons_Cancel, (_, _) => { })
            .SetPositiveButton(Translator.Buttons_OK, (_, _) => picked(Format(new Color(r, g, b, allowsAlpha ? a : 255), allowsAlpha)))
            .Show();
    }

    public static string Format(Color color, bool withAlpha)
        => withAlpha
            ? string.Create(CultureInfo.InvariantCulture, $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}")
            : string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}");

    private sealed class SliderChanged(Action<float, bool> changed) : Java.Lang.Object, Slider.IOnChangeListener
    {
        public void OnValueChange(Slider slider, float value, bool fromUser) => changed(value, fromUser);
    }
}

/// <summary>The shared appearance model plus the Android wallpaper backdrop switch.</summary>
public sealed class AndroidAppearanceModel : AppearanceSettingsModel
{
    private readonly AndroidThemeService _themes;

    public AndroidAppearanceModel(AndroidThemeService themes) : base(themes)
    {
        _themes = themes;
        _themes.AppearanceChanged += AppearanceChanged;
    }

    public bool IsBackdropEnabled
    {
        get => _themes.IsBackdropEnabled;
        set
        {
            if (value == _themes.IsBackdropEnabled) return;
            _themes.IsBackdropEnabled = value;
            OnPropertyChanged();
        }
    }

    protected override void NotifyAppearanceChanged()
    {
        base.NotifyAppearanceChanged();
        OnPropertyChanged(nameof(IsBackdropEnabled));
    }

    private void AppearanceChanged(object? sender, EventArgs args) => NotifyAppearanceChanged();

    public override void Dispose()
    {
        _themes.AppearanceChanged -= AppearanceChanged;
        base.Dispose();
    }
}
