using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Intelligence;

/// <summary>
/// The Windows WinoIntelligencePromotionIllustration ("long thread, short summary", macOS
/// WinoIntelligencePromotionIllustrationView): the same 128x128 canvas shapes and theme colours, scaled uniformly.
/// </summary>
public sealed class WinoIntelligencePromotionIllustration : IDisposable
{
    private readonly Gtk.DrawingArea _area = Gtk.DrawingArea.New();
    private readonly Box _self;
    private readonly IDisposable _styleChanged;

    public WinoIntelligencePromotionIllustration(int size = 96)
    {
        WinoLiveObjects.Track(this);
        _self = new Box(this);
        var self = _self;
        _area.SetContentWidth(size);
        _area.SetContentHeight(size);
        _area.SetValign(Gtk.Align.Center);
        _area.SetHalign(Gtk.Align.Center);
        _area.SetDrawFunc((_, cr, width, height) => self.Value?.Draw(cr, width, height));
        _styleChanged = GtkSignals.OnNotify(Adw.StyleManager.GetDefault(), "dark", () => self.Value?._area.QueueDraw());
    }

    public Gtk.Widget Root => _area;

    public void Dispose()
    {
        _self.Value = null;
        _styleChanged.Dispose();
    }

    private void Draw(Cairo.Context cr, int width, int height)
    {
        var dark = IntelligencePaint.IsDark;
        var surface = dark ? IntelligencePaint.Rgb(0x3C3C43) : (1.0, 1.0, 1.0);
        var line = IntelligencePaint.Rgb(dark ? 0x52525Du : 0xD6D6E0u);
        var lineSoft = IntelligencePaint.Rgb(dark ? 0x46464Fu : 0xE9E9F1u);
        var warm = IntelligencePaint.Rgb(dark ? 0xF5C05Cu : 0xF2B33Du);
        var brand = IntelligencePaint.Solid(dark);
        var scale = Math.Min(width, height) / 128.0;
        cr.Save();
        cr.Translate((width - 128 * scale) / 2, (height - 128 * scale) / 2);
        cr.Scale(scale, scale);

        Ellipse(cr, 28, 99, 68, 10);
        Fill(cr, dark ? (0, 0, 0) : IntelligencePaint.Rgb(0x141428), dark ? 0x52 / 255.0 : 0x12 / 255.0);
        Rounded(cr, 28, 22, 78, 52, 10);
        Fill(cr, brand, 0.2);
        Rounded(cr, 18, 38, 78, 60, 10);
        cr.SetSourceRgba(surface.Item1, surface.Item2, surface.Item3, 1);
        cr.FillPreserve();
        cr.SetSourceRgba(line.R, line.G, line.B, 1);
        cr.LineWidth = 1.5;
        cr.Stroke();
        Rounded(cr, 29, 50, 52, 4.5, 2.25);
        Fill(cr, line);
        Rounded(cr, 29, 59, 44, 4, 2);
        Fill(cr, lineSoft);
        Rounded(cr, 29, 68, 48, 4, 2);
        Fill(cr, lineSoft);
        Rounded(cr, 29, 80, 34, 5.5, 2.75);
        Fill(cr, brand);
        Rounded(cr, 29, 89, 22, 4.5, 2.25);
        Fill(cr, brand, 0.45);
        Ellipse(cr, 84, 12, 36, 36);
        Fill(cr, brand, 0.2);

        // The spark is the only shape that carries the brand gradient.
        Spark(cr, 102, 17, 115, 30, 102, 43, 89, 30, 2.08, 10.92);
        IntelligencePaint.SetGradient(cr, 89, 17, 26, 26, dark);
        cr.Fill();
        Spark(cr, 114, 45, 119, 50, 114, 55, 109, 50, 0.8, 4.2);
        Fill(cr, warm);
        cr.Restore();
    }

    private static void Fill(Cairo.Context cr, (double R, double G, double B) color, double alpha = 1)
    {
        cr.SetSourceRgba(color.R, color.G, color.B, alpha);
        cr.Fill();
    }

    private static void Ellipse(Cairo.Context cr, double x, double y, double width, double height)
    {
        cr.Save();
        cr.Translate(x + width / 2, y + height / 2);
        cr.Scale(width / 2, height / 2);
        cr.NewSubPath();
        cr.Arc(0, 0, 1, 0, 2 * Math.PI);
        cr.Restore();
    }

    private static void Rounded(Cairo.Context cr, double x, double y, double width, double height, double radius)
    {
        cr.NewSubPath();
        cr.Arc(x + width - radius, y + radius, radius, -Math.PI / 2, 0);
        cr.Arc(x + width - radius, y + height - radius, radius, 0, Math.PI / 2);
        cr.Arc(x + radius, y + height - radius, radius, Math.PI / 2, Math.PI);
        cr.Arc(x + radius, y + radius, radius, Math.PI, 3 * Math.PI / 2);
        cr.ClosePath();
    }

    /// <summary>The four-point star from the XAML: quadratic curves through the inner control points, as cubic curves.</summary>
    private static void Spark(Cairo.Context cr, double tx, double ty, double rx, double ry, double bx, double by, double lx, double ly, double near, double far)
    {
        cr.NewPath();
        cr.MoveTo(tx, ty);
        Quad(cr, tx, ty, tx + near, ty + far, rx, ry);
        Quad(cr, rx, ry, tx + near, ry + near, bx, by);
        Quad(cr, bx, by, tx - near, ry + near, lx, ly);
        Quad(cr, lx, ly, tx - near, ty + far, tx, ty);
        cr.ClosePath();
    }

    private static void Quad(Cairo.Context cr, double fx, double fy, double cx, double cy, double x, double y)
        => cr.CurveTo(fx + 2.0 / 3 * (cx - fx), fy + 2.0 / 3 * (cy - fy), x + 2.0 / 3 * (cx - x), y + 2.0 / 3 * (cy - y), x, y);

    private sealed class Box(WinoIntelligencePromotionIllustration value)
    {
        public WinoIntelligencePromotionIllustration? Value { get; set; } = value;
    }
}
