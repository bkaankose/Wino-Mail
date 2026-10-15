using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Views;
using AndroidX.Core.Graphics;
using Wino.Core.Domain.Enums;
using Wino.Presentation.Android;
using AndroidPath = Android.Graphics.Path;

namespace Wino.Mail.Android.Views.Settings.WinoAccount;

/// <summary>Shared drawing for the vector illustrations: rounded rectangles, ovals and SVG path data in canvas units.</summary>
internal abstract class CanvasIllustrationView : View
{
    private readonly Paint _paint = new(PaintFlags.AntiAlias);

    protected CanvasIllustrationView(Context context) : base(context)
    {
        ImportantForAccessibility = ImportantForAccessibility.No;
    }

    protected CanvasIllustrationView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    protected Canvas? Target { get; private set; }

    /// <summary>Draws <see cref="DrawContent"/> scaled uniformly from a <paramref name="width"/> x <paramref name="height"/> canvas, centred.</summary>
    protected void DrawScaled(Canvas canvas, double width, double height)
    {
        if (Width <= 0 || Height <= 0 || Context is not { } context) return;
        var scale = (float)Math.Min(Width / width, Height / height);
        canvas.Save();
        canvas.Translate((float)((Width - width * scale) / 2), (float)((Height - height * scale) / 2));
        canvas.Scale(scale, scale);
        Target = canvas;
        try { DrawContent(context); }
        finally
        {
            Target = null;
            canvas.Restore();
        }
    }

    /// <summary>Draws the shapes; colours come from <paramref name="context"/>'s tokens.</summary>
    protected abstract void DrawContent(Context context);

    protected void Rect(double x, double y, double w, double h, double r, Color fill, Color? stroke = null, double strokeWidth = 1.5)
    {
        if (Target is not { } canvas) return;
        var bounds = new RectF((float)x, (float)y, (float)(x + w), (float)(y + h));
        Fill(fill);
        canvas.DrawRoundRect(bounds, (float)r, (float)r, _paint);
        if (stroke is not { } color) return;
        Stroke(color, strokeWidth);
        canvas.DrawRoundRect(bounds, (float)r, (float)r, _paint);
    }

    protected void Ellipse(double x, double y, double w, double h, Color fill)
    {
        if (Target is not { } canvas) return;
        Fill(fill);
        canvas.DrawOval(new RectF((float)x, (float)y, (float)(x + w), (float)(y + h)), _paint);
    }

    protected void StrokePath(string data, Color stroke, double width, bool round = true, float[]? dash = null)
    {
        if (Target is not { } canvas || PathParser.CreatePathFromPathData(data) is not { } path) return;
        Stroke(stroke, width);
        _paint.StrokeJoin = round ? Paint.Join.Round : Paint.Join.Miter;
        if (dash is not null) _paint.SetPathEffect(new DashPathEffect(dash, 0));
        canvas.DrawPath(path, _paint);
        _paint.SetPathEffect(null);
        _paint.StrokeJoin = Paint.Join.Miter;
    }

    protected void FillPath(string data, Color fill)
    {
        if (Target is not { } canvas || PathParser.CreatePathFromPathData(data) is not { } path) return;
        Fill(fill);
        canvas.DrawPath(path, _paint);
    }

    protected void FillPath(AndroidPath path, Color fill)
    {
        if (Target is not { } canvas) return;
        Fill(fill);
        canvas.DrawPath(path, _paint);
    }

    protected void FillPath(AndroidPath path, Shader shader)
    {
        if (Target is not { } canvas) return;
        _paint.SetStyle(Paint.Style.Fill);
        // The shader supplies the colour; an opaque base keeps it fully opaque.
        if (Context is { } context) _paint.Color = WinoTokens.Color(context, Resource.Color.wino_winoaccount_gradient_base);
        _paint.SetShader(shader);
        canvas.DrawPath(path, _paint);
        _paint.SetShader(null);
    }

    private void Fill(Color color)
    {
        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = color;
    }

    private void Stroke(Color color, double width)
    {
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = (float)width;
        _paint.Color = color;
    }

    protected static Color Faded(Color color, double opacity) => WinoColors.WithAlpha(color, color.A / 255.0 * opacity);
}

/// <summary>
/// The four offer illustrations of the Windows page (iOS WinoAccountBenefitIllustrationView), drawn from the same
/// 240x170 canvas shapes and scaled uniformly: Move to a new device, Add-ons follow you, Wino Intelligence and Unlimited
/// Accounts. Colours are semantic (accent, divider, text, card) so light and dark both work.
/// </summary>
internal sealed class WinoAccountBenefitIllustrationView : CanvasIllustrationView
{
    private const double CanvasWidth = 240;
    private const double CanvasHeight = 170;
    private WinoAccountBenefitType? _kind;

    public WinoAccountBenefitIllustrationView(Context context) : base(context) { }

    private WinoAccountBenefitIllustrationView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public WinoAccountBenefitType? Kind
    {
        get => _kind;
        set
        {
            _kind = value;
            Invalidate();
        }
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        var width = MeasureSpec.GetSize(widthMeasureSpec);
        SetMeasuredDimension(width, (int)Math.Round(width * CanvasHeight / CanvasWidth));
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_kind is not null) DrawScaled(canvas, CanvasWidth, CanvasHeight);
    }

    private Color Card { get; set; }
    private Color Divider { get; set; }
    private Color AccentColor { get; set; }
    private Color PrimaryText { get; set; }
    private Color Check { get; set; }

    private Color Accent(double opacity = 1) => Faded(AccentColor, opacity);

    /// <summary>Windows TextFillColorTertiary at an extra opacity.</summary>
    private Color Tertiary(double opacity) => Faded(PrimaryText, 0.45 * opacity);

    protected override void DrawContent(Context context)
    {
        Card = WinoColors.CardFill(context);
        Divider = WinoColors.Divider(context);
        AccentColor = WinoColors.Accent(context);
        PrimaryText = WinoColors.PrimaryText(context);
        Check = WinoTokens.Color(context, Resource.Color.wino_winoaccount_benefit_check);
        switch (_kind)
        {
            case WinoAccountBenefitType.DeviceTransfer: DrawDeviceTransfer(); break;
            case WinoAccountBenefitType.Entitlements: DrawEntitlements(); break;
            case WinoAccountBenefitType.Intelligence: DrawIntelligence(); break;
            case WinoAccountBenefitType.UnlimitedAccounts: DrawUnlimitedAccounts(); break;
        }
    }

    private void DrawDeviceTransfer()
    {
        // Two devices: the old one in neutral strokes, the new one in the accent.
        Rect(6, 34, 88, 98, 9, Card, Divider);
        Rect(16, 46, 52, 7, 3.5, Tertiary(0.5));
        Rect(16, 61, 68, 6, 3, Tertiary(0.28));
        Rect(16, 75, 68, 6, 3, Tertiary(0.28));
        Rect(16, 89, 46, 6, 3, Tertiary(0.28));
        Rect(16, 107, 34, 14, 7, Accent(0.8));

        Rect(146, 34, 88, 98, 9, Card, Accent());
        Rect(156, 46, 52, 7, 3.5, Accent(0.55));
        Rect(156, 61, 68, 6, 3, Tertiary(0.28));
        Rect(156, 75, 68, 6, 3, Tertiary(0.28));
        Rect(156, 89, 46, 6, 3, Tertiary(0.28));
        Rect(156, 107, 34, 14, 7, Accent(0.8));

        // StrokeDashArray "1,3" is in stroke widths (2 units).
        StrokePath("M96,62 C112,40 128,40 144,62", Accent(), 2, round: false, dash: [2, 6]);
        StrokePath("M138,56 L145,62 L138,68", Accent(), 2);
        Ellipse(101, 81, 38, 38, Accent());
        StrokePath("M112,100 L118,106 L128,94", Check, 2.4);
    }

    private void DrawEntitlements()
    {
        StrokePath("M43,92 V72 H120 V56 M120,72 H197 V92", Divider, 1.5, round: false);
        Rect(14, 92, 58, 48, 7, Card, Divider);
        Rect(91, 92, 58, 48, 7, Card, Divider);
        Rect(168, 92, 58, 48, 7, Card, Divider);

        const string shield = "M120,22 L152,36 V56 C152,73 139.2,86.4 120,94 C100.8,86.4 88,73 88,56 V36 Z";
        FillPath(shield, Accent(0.16));
        StrokePath(shield, Accent(), 1.8);
        StrokePath("M108,55.5 L116,63.5 L133,46.5", Accent(), 2.6);
        StrokePath("M35,112 L40,117 L50,107 M112,112 L117,117 L127,107 M189,112 L194,117 L204,107", Accent(), 2);
    }

    private void DrawIntelligence()
    {
        Rect(34, 22, 172, 126, 10, Card, Divider);
        Rect(50, 42, 66, 8, 4, Tertiary(0.45));
        Rect(50, 62, 140, 7, 3.5, Tertiary(0.25));
        Rect(50, 78, 140, 7, 3.5, Tertiary(0.25));
        Rect(50, 94, 96, 7, 3.5, Tertiary(0.25));
        Rect(44, 112, 152, 26, 7, Accent(0.15));
        Rect(56, 121, 104, 8, 4, Accent(0.7));
        FillPath("M183.3,16 L188.5,32 L204.5,37.2 L188.5,42.4 L183.3,58.4 L178.1,42.4 L162.1,37.2 L178.1,32 Z", Accent());
        FillPath("M206.9,44 L209.6,52 L217.6,54.7 L209.6,57.4 L206.9,65.4 L204.2,57.4 L196.2,54.7 L204.2,52 Z", Accent(0.65));
    }

    private void DrawUnlimitedAccounts()
    {
        Rect(26, 30, 128, 34, 8, Card, Divider);
        Ellipse(36, 37, 20, 20, Accent(0.55));
        Rect(64, 41, 62, 7, 3.5, Tertiary(0.4));
        Rect(64, 52, 40, 5, 2.5, Tertiary(0.22));

        Rect(40, 72, 128, 34, 8, Card, Divider);
        Ellipse(50, 79, 20, 20, Accent(0.4));
        Rect(78, 83, 62, 7, 3.5, Tertiary(0.4));
        Rect(78, 94, 40, 5, 2.5, Tertiary(0.22));

        Rect(54, 114, 128, 34, 8, Card, Accent());
        Ellipse(64, 121, 20, 20, Accent(0.28));
        Rect(92, 125, 62, 7, 3.5, Tertiary(0.4));
        Rect(92, 136, 40, 5, 2.5, Tertiary(0.22));

        // The right lobe runs the other way round, so the strokes cross and read as an infinity mark.
        StrokePath("M196,47 C186,31 176,27 168,31 C158,36 158,58 168,63 C176,67 186,63 196,47 C206,63 216,67 224,63 C234,58 234,36 224,31 C216,27 206,31 196,47 Z", Accent(), 3);
    }
}

/// <summary>
/// Port of the Windows WinoIntelligencePromotionIllustration ("long thread, short summary"): the same 128x128 canvas
/// shapes and theme colours, scaled uniformly to the view.
/// </summary>
internal sealed class WinoIntelligencePromotionIllustrationView : CanvasIllustrationView
{
    private readonly int _size;

    /// <param name="size">The square size as a dimension token.</param>
    public WinoIntelligencePromotionIllustrationView(Context context, int size) : base(context)
    {
        _size = WinoTokens.Px(context, size);
    }

    private WinoIntelligencePromotionIllustrationView(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) => SetMeasuredDimension(_size, _size);

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        DrawScaled(canvas, 128, 128);
    }

    protected override void DrawContent(Context context)
    {
        var surface = WinoTokens.Color(context, Resource.Color.wino_winoaccount_promo_surface);
        var line = WinoTokens.Color(context, Resource.Color.wino_winoaccount_promo_line);
        var lineSoft = WinoTokens.Color(context, Resource.Color.wino_winoaccount_promo_line_soft);
        var warm = WinoTokens.Color(context, Resource.Color.wino_winoaccount_promo_warm);
        var shadow = WinoTokens.Color(context, Resource.Color.wino_winoaccount_promo_shadow);
        var brand = IntelligenceViews.BrandSolid(context);

        Ellipse(28, 99, 68, 10, shadow);
        Rect(28, 22, 78, 52, 10, Faded(brand, 0.2));
        Rect(18, 38, 78, 60, 10, surface, line, 1.5);
        Rect(29, 50, 52, 4.5, 2.25, line);
        Rect(29, 59, 44, 4, 2, lineSoft);
        Rect(29, 68, 48, 4, 2, lineSoft);
        Rect(29, 80, 34, 5.5, 2.75, brand);
        Rect(29, 89, 22, 4.5, 2.25, Faded(brand, 0.45));
        Ellipse(84, 12, 36, 36, Faded(brand, 0.2));

        // The spark: the only shape that carries the brand gradient.
        FillPath(Spark(102, 17, 115, 30, 102, 43, 89, 30, 2.08, 10.92),
            new LinearGradient(89, 17, 115, 43, IntelligenceViews.BrandGradient(context), IntelligenceViews.BrandGradientStops,
                Shader.TileMode.Clamp ?? throw new InvalidOperationException("No tile mode.")));
        FillPath(Spark(114, 45, 119, 50, 114, 55, 109, 50, 0.8, 4.2), warm);
    }

    /// <summary>The four-point star path from the XAML (quadratic curves through the inner control points).</summary>
    private static AndroidPath Spark(double tx, double ty, double rx, double ry, double bx, double by, double lx, double ly, double near, double far)
    {
        var path = new AndroidPath();
        path.MoveTo((float)tx, (float)ty);
        path.QuadTo((float)(tx + near), (float)(ty + far), (float)rx, (float)ry);
        path.QuadTo((float)(tx + near), (float)(ry + near), (float)bx, (float)by);
        path.QuadTo((float)(tx - near), (float)(ry + near), (float)lx, (float)ly);
        path.QuadTo((float)(tx - near), (float)(ty + far), (float)tx, (float)ty);
        path.Close();
        return path;
    }
}
