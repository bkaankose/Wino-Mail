using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Google.Android.Material.MaterialSwitch;
using Google.Android.Material.ProgressIndicator;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Settings.WinoAccount;

/// <summary>
/// Small factory helpers shared by the Wino Account and Intelligence pages (iOS IntelligenceViews and
/// WinoIntelligenceBrand): card surfaces, pills, spinners, meters, link buttons, the delayed progress row and the
/// brand chips.
/// </summary>
internal static class IntelligenceViews
{
    private static int Px(Context context, int dimen) => WinoTokens.Px(context, dimen);

    /// <summary>Flat brand colour (WinoIllustrationBrandSolidBrush).</summary>
    public static Color BrandSolid(Context context) => WinoTokens.Color(context, Resource.Color.wino_winoaccount_brand_solid);

    /// <summary>The Windows WinoIntelligenceBrandIconBrush stops.</summary>
    public static int[] BrandGradient(Context context) =>
    [
        WinoTokens.Color(context, Resource.Color.wino_winoaccount_brand_gradient_1).ToArgb(),
        WinoTokens.Color(context, Resource.Color.wino_winoaccount_brand_gradient_2).ToArgb(),
        WinoTokens.Color(context, Resource.Color.wino_winoaccount_brand_gradient_3).ToArgb(),
        WinoTokens.Color(context, Resource.Color.wino_winoaccount_brand_gradient_4).ToArgb()
    ];

    public static readonly float[] BrandGradientStops = [0f, 0.42f, 0.66f, 1f];

    /// <summary>
    /// A rounded fill with an optional stroke; <paramref name="radius"/> and <paramref name="strokeWidth"/> are dimension
    /// tokens.
    /// </summary>
    public static GradientDrawable Shape(Context context, Color fill, Color? stroke, int radius, int strokeWidth = 0)
    {
        var shape = new GradientDrawable();
        shape.SetColor(fill);
        shape.SetCornerRadius(WinoTokens.PxF(context, radius));
        if (stroke is { } color) shape.SetStroke(Math.Max(1, Px(context, strokeWidth == 0 ? Resource.Dimension.wino_winoaccount_stroke : strokeWidth)), color);
        return shape;
    }

    /// <summary>A filled circle (dots, the photo veil and its badge), with an optional stroke.</summary>
    public static GradientDrawable Oval(Context context, Color fill, Color? stroke = null)
    {
        var shape = new GradientDrawable();
        shape.SetShape(ShapeType.Oval);
        shape.SetColor(fill);
        if (stroke is { } color) shape.SetStroke(Math.Max(1, Px(context, Resource.Dimension.wino_winoaccount_stroke)), color);
        return shape;
    }

    /// <summary>A card surface like the Windows Border cards (card fill, card stroke); padding and radius are dimension tokens.</summary>
    public static FrameLayout Surface(Context context, View content, int padding = Resource.Dimension.wino_space_xl, int radius = Resource.Dimension.wino_radius_medium)
    {
        var surface = new FrameLayout(context) { Background = Shape(context, WinoColors.CardFill(context), WinoColors.CardStroke(context), radius) };
        int inset = padding == 0 ? 0 : Px(context, padding);
        surface.SetPadding(inset, inset, inset, inset);
        surface.AddView(content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        return surface;
    }

    /// <summary>A rounded status pill (Active, Not active, Unlocked).</summary>
    public static TextView Pill(Context context, string text, Color textColor, Color fill, Color? stroke = null)
    {
        var label = WinoTokens.Style(new TextView(context) { Text = text, Background = Shape(context, fill, stroke, Resource.Dimension.wino_radius_pill) }, WinoTextStyle.Caption);
        label.SetTextColor(textColor);
        label.SetSingleLine(true);
        WinoTokens.Pad(label, Resource.Dimension.wino_space_sm, Resource.Dimension.wino_space_3xs, Resource.Dimension.wino_space_sm, Resource.Dimension.wino_space_3xs);
        return label;
    }

    public static TextView SuccessPill(Context context, string text)
        => Pill(context, text, WinoColors.Success(context), WinoTokens.Color(context, Resource.Color.wino_winoaccount_success_pill_fill));

    public static TextView NeutralPill(Context context, string text)
        => Pill(context, text, WinoColors.SecondaryText(context), WinoColors.SubtleFill(context), WinoColors.CardStroke(context));

    /// <summary>An indeterminate ring (<paramref name="size"/> is a dimension token), hidden until <see cref="SetSpinning"/> starts it.</summary>
    public static CircularProgressIndicator Spinner(Context context, int size = Resource.Dimension.wino_glyph_m)
    {
        var spinner = new CircularProgressIndicator(context)
        {
            Indeterminate = true,
            IndicatorSize = Px(context, size),
            TrackThickness = Px(context, Resource.Dimension.wino_space_3xs),
            Visibility = ViewStates.Gone
        };
        spinner.SetIndicatorColor(WinoColors.Accent(context).ToArgb());
        return spinner;
    }

    public static void SetSpinning(View spinner, bool spinning) => spinner.Visibility = spinning ? ViewStates.Visible : ViewStates.Gone;

    /// <summary>A thin rounded meter (Windows ProgressBar), 0 to 100.</summary>
    public static LinearProgressIndicator Meter(Context context)
    {
        var meter = new LinearProgressIndicator(context)
        {
            Max = 100,
            TrackThickness = Px(context, Resource.Dimension.wino_space_2xs),
            TrackCornerRadius = Px(context, Resource.Dimension.wino_space_3xs)
        };
        meter.SetIndicatorColor(WinoColors.Accent(context).ToArgb());
        meter.TrackColor = WinoTokens.Color(context, Resource.Color.wino_winoaccount_meter_track).ToArgb();
        return meter;
    }

    public static void SetMeter(LinearProgressIndicator meter, double value, bool error = false)
    {
        var context = meter.Context;
        meter.SetProgressCompat((int)Math.Round(Math.Clamp(value, 0, 100)), false);
        if (context is not null) meter.SetIndicatorColor((error ? WinoColors.Critical(context) : WinoColors.Accent(context)).ToArgb());
    }

    /// <summary>The page-level first-load state: a centred spinner shown until cached content is ready.</summary>
    public static FrameLayout LoadingState(Context context)
    {
        var host = new FrameLayout(context);
        var spinner = new CircularProgressIndicator(context) { Indeterminate = true, IndicatorSize = Px(context, Resource.Dimension.wino_winoaccount_loading_spinner) };
        spinner.SetIndicatorColor(WinoColors.Accent(context).ToArgb());
        int margin = Px(context, Resource.Dimension.wino_winoaccount_loading_margin);
        host.AddView(spinner, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.CenterHorizontal)
        {
            TopMargin = margin,
            BottomMargin = margin
        });
        return host;
    }

    /// <summary>
    /// Visibility for a transient progress row: it appears only when the work outlasts <paramref name="delayMs"/>
    /// (quick refreshes never shift the layout) and hides as soon as the work ends. <paramref name="alive"/> stops a
    /// pending show after the page's view is released.
    /// </summary>
    public static Action<bool> DelayedProgressRow(View row, View spinner, Func<bool> alive, long delayMs = 600)
    {
        var generation = 0;
        var handler = new Handler(Looper.MainLooper ?? throw new InvalidOperationException("No main looper."));
        row.Visibility = ViewStates.Gone;
        SetSpinning(spinner, false);
        return on =>
        {
            if (on && row.Visibility == ViewStates.Visible) return;
            var current = ++generation;
            if (!on)
            {
                row.Visibility = ViewStates.Gone;
                SetSpinning(spinner, false);
                return;
            }
            handler.PostDelayed(() =>
            {
                if (current != generation || !alive()) return;
                row.Visibility = ViewStates.Visible;
                SetSpinning(spinner, true);
            }, delayMs);
        };
    }

    /// <summary>An accent text button (Windows HyperlinkButton) with the large touch height.</summary>
    public static MaterialButton LinkButton(Context context, string title)
    {
        var button = new MaterialButton(context, null, Resource.Attribute.borderlessButtonStyle) { Text = title };
        button.SetAllCaps(false);
        button.SetTextColor(WinoColors.AccentText(context));
        button.InsetTop = 0;
        button.InsetBottom = 0;
        button.SetMinHeight(Px(context, Resource.Dimension.wino_size_touch_large));
        return button;
    }

    /// <summary>Opens a web page in the browser through the platform launcher.</summary>
    public static void OpenUrl(string url)
    {
        if (MainApplication.Services?.GetService<IExternalLauncher>() is not { } launcher) return;
        _ = Launch(launcher, url);

        static async Task Launch(IExternalLauncher launcher, string url)
        {
            try { await launcher.LaunchUriAsync(new Uri(url)); }
            catch (Exception exception) { Serilog.Log.Warning(exception, "Opening {Url} failed.", url); }
        }
    }

    /// <summary>Wrapping flow of views (Windows WrapPanel): a Material ChipGroup lays any children out in rows, <paramref name="spacing"/> (a dimension token) apart.</summary>
    public static ChipGroup Flow(Context context, IEnumerable<View> items, int spacing)
    {
        var flow = new ChipGroup(context) { SingleLine = false };
        flow.SetChipSpacing(Px(context, spacing));
        foreach (var item in items) flow.AddView(item, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent));
        return flow;
    }

    /// <summary>The promotion pill used in the not-subscribed card: light capsule, brand glyph, caption.</summary>
    public static View Chip(Context context, WinoIconGlyph glyph, string text)
    {
        var row = new LinearLayout(context)
        {
            Orientation = Orientation.Horizontal,
            Background = Shape(context, WinoTokens.Color(context, Resource.Color.wino_winoaccount_chip_fill), WinoColors.CardStroke(context), Resource.Dimension.wino_radius_pill),
            ContentDescription = text
        };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetMinimumHeight(Px(context, Resource.Dimension.wino_winoaccount_chip_height));
        WinoTokens.Pad(row, Resource.Dimension.wino_space_sm, Resource.Dimension.wino_space_2xs, Resource.Dimension.wino_space_sm, Resource.Dimension.wino_space_2xs);
        row.AddView(new BrandIconView(context, glyph, Resource.Dimension.wino_glyph_xs));
        var label = WinoTokens.Style(new TextView(context) { Text = text, ImportantForAccessibility = ImportantForAccessibility.No }, WinoTextStyle.Caption);
        label.SetTextColor(WinoColors.PrimaryText(context));
        row.AddView(label, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = Px(context, Resource.Dimension.wino_space_xs) });
        return row;
    }

    /// <summary>A text view in the Wino type scale (static twin of the page's Text helper, for controls).</summary>
    public static TextView Label(Context context, string? text, WinoTextStyle style = WinoTextStyle.Body, int maxLines = 0)
    {
        var label = WinoTokens.Style(new TextView(context) { Text = text ?? string.Empty }, style);
        if (maxLines > 0)
        {
            label.SetMaxLines(maxLines);
            label.Ellipsize = global::Android.Text.TextUtils.TruncateAt.End;
        }
        return label;
    }

    /// <summary>A vertical stack whose children stretch, <paramref name="spacing"/> (a dimension token; 0 for none) apart.</summary>
    public static LinearLayout VStack(Context context, int spacing, params View[] views)
    {
        var stack = new LinearLayout(context) { Orientation = Orientation.Vertical };
        foreach (var view in views) Append(stack, view, spacing);
        return stack;
    }

    public static void Append(LinearLayout stack, View view, int spacing)
    {
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        if (stack.ChildCount > 0 && spacing != 0) layout.TopMargin = Px(stack.Context ?? throw new InvalidOperationException("No context."), spacing);
        stack.AddView(view, layout);
    }

    /// <summary>A horizontal row, <paramref name="spacing"/> (a dimension token) apart; <paramref name="fill"/> takes the remaining width.</summary>
    public static LinearLayout HStack(Context context, int spacing, View? fill, params View[] views)
    {
        var row = new LinearLayout(context) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        foreach (var view in views)
        {
            var layout = ReferenceEquals(view, fill)
                ? new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1)
                : new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            if (row.ChildCount > 0) layout.MarginStart = Px(context, spacing);
            row.AddView(view, layout);
        }
        return row;
    }

    /// <summary>A flexible gap for horizontal rows.</summary>
    public static View Spacer(Context context) => new(context);

    /// <summary>A hairline divider in the card stroke colour.</summary>
    public static View Separator(Context context)
    {
        var line = new View(context);
        line.SetBackgroundColor(WinoColors.CardStroke(context));
        line.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Math.Max(1, Px(context, Resource.Dimension.wino_space_hairline)));
        return line;
    }
}

/// <summary>A Wino glyph filled with the brand gradient instead of a flat tint (iOS BrandIconView).</summary>
public class BrandIconView : WinoIconView
{
    /// <param name="size">The glyph size as a dimension token.</param>
    public BrandIconView(Context context, WinoIconGlyph glyph, int size) : base(context)
    {
        Icon = glyph;
        GlyphSize = WinoTokens.Dp(context, size);
        SetTextColor(WinoTokens.Color(context, Resource.Color.wino_winoaccount_gradient_base));
        int box = WinoTokens.Px(context, size);
        SetMinimumWidth(box);
        SetMinimumHeight(box);
    }

    protected BrandIconView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        if (Context is not { } context || w <= 0 || h <= 0) return;
        Paint?.SetShader(new LinearGradient(0, 0, w, h, IntelligenceViews.BrandGradient(context),
            IntelligenceViews.BrandGradientStops, Shader.TileMode.Clamp ?? throw new InvalidOperationException("No tile mode.")));
        Invalidate();
    }
}

/// <summary>
/// A Windows ToggleSwitch with custom On/Off content (Allowed / Not allowed): the state label, then a MaterialSwitch.
/// <see cref="Toggled"/> is raised only for the user's changes.
/// </summary>
internal sealed class WinoTextSwitch : LinearLayout
{
    private readonly TextView _label;
    private readonly MaterialSwitch _switch;
    private readonly string _on;
    private readonly string _off;
    private bool _applying;

    public WinoTextSwitch(Context context, string on, string off, string accessibilityLabel) : base(context)
    {
        _on = on;
        _off = off;
        Orientation = Orientation.Horizontal;
        SetGravity(GravityFlags.CenterVertical);
        _label = IntelligenceViews.Label(context, off, WinoTextStyle.BodySecondary);
        _label.ImportantForAccessibility = ImportantForAccessibility.No;
        _label.Gravity = GravityFlags.End;
        var paint = _label.Paint;
        if (paint is not null) _label.SetMinWidth((int)Math.Ceiling(Math.Max(paint.MeasureText(on), paint.MeasureText(off))));
        _switch = WinoMaterialAccent.Tint(new MaterialSwitch(context) { ContentDescription = accessibilityLabel });
        AddView(_label);
        AddView(_switch, new LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoTokens.Px(context, Resource.Dimension.wino_space_s) });
        _switch.CheckedChange += (_, _) =>
        {
            Update();
            if (!_applying) Toggled?.Invoke(this, EventArgs.Empty);
        };
        Update();
    }

    /// <summary>Raised after the user flips the switch (not when <see cref="IsOn"/> is set in code).</summary>
    public event EventHandler? Toggled;

    public bool IsOn
    {
        get => _switch.Checked;
        set
        {
            if (_switch.Checked == value) return;
            _applying = true;
            try { _switch.Checked = value; }
            finally { _applying = false; }
        }
    }

    public bool IsEnabled
    {
        get => _switch.Enabled;
        set
        {
            _switch.Enabled = value;
            _label.Alpha = value ? 1f : 0.55f;
        }
    }

    private void Update()
    {
        _label.Text = IsOn ? _on : _off;
        if (OperatingSystem.IsAndroidVersionAtLeast(30)) _switch.StateDescription = _label.Text;
    }
}
