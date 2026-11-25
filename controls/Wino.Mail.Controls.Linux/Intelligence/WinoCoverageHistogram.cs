using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Intelligence;

/// <summary>One histogram column: the three stacked heights in pixels (indexed at the bottom, then to index, then left out) and its tooltip.</summary>
public readonly record struct WinoCoverageBucket(double Indexed, double ToIndex, double Outside, string? ToolTip);

/// <summary>Coverage band colours (macOS CoverageColors): indexed green, to index the accent, left out the foreground at a low alpha.</summary>
public static class WinoCoverageColors
{
    public static (double R, double G, double B, double A) Indexed(bool dark) => dark ? (0x6C / 255.0, 0xCB / 255.0, 0x5F / 255.0, 1) : (0x0F / 255.0, 0x7B / 255.0, 0x0F / 255.0, 1);

    public static (double R, double G, double B, double A) ToIndex()
    {
        var accent = Adw.StyleManager.GetDefault().GetAccentColorRgba();
        return (accent.Red, accent.Green, accent.Blue, 1);
    }

    public static (double R, double G, double B, double A) Outside(bool dark) => dark ? (1, 1, 1, 0.24) : (0, 0, 0, 0.22);

    public static string Hex((double R, double G, double B, double A) color)
        => $"rgba({(int)(color.R * 255)},{(int)(color.G * 255)},{(int)(color.B * 255)},{color.A.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)})";
}

/// <summary>
/// The coverage histogram (Windows CoverageBucketTemplate, macOS CoverageHistogramView): one column per bucket in the
/// given order, left to right, stacked bottom-up. The tooltip follows the column under the pointer.
/// </summary>
public sealed class WinoCoverageHistogram : IDisposable
{
    private const double Gap = 2;

    private readonly Gtk.DrawingArea _area = Gtk.DrawingArea.New();
    private readonly Box _self;
    private readonly List<IDisposable> _subscriptions = [];
    private WinoCoverageBucket[] _buckets = [];
    private int _hovered = -1;

    public WinoCoverageHistogram(int height = 88)
    {
        WinoLiveObjects.Track(this);
        _self = new Box(this);
        var self = _self;
        _area.SetContentHeight(height);
        _area.SetHexpand(true);
        // The draw func stays rooted until the area is destroyed: it reaches this object only through the box Dispose empties.
        _area.SetDrawFunc((_, cr, width, h) => self.Value?.Draw(cr, width, h));
        var style = Adw.StyleManager.GetDefault();
        _subscriptions.Add(GtkSignals.OnNotify(style, "dark", () => self.Value?._area.QueueDraw()));
        _subscriptions.Add(GtkSignals.OnNotify(style, "accent-color", () => self.Value?._area.QueueDraw()));
        var motion = Gtk.EventControllerMotion.New();
        _subscriptions.Add(GtkSignals.Connect<Gtk.EventControllerMotion, Gtk.EventControllerMotion.MotionSignalArgs>(
            h => motion.OnMotion += h, h => motion.OnMotion -= h, (_, args) => self.Value?.Hover(args.X)));
        _area.AddController(motion);
    }

    public Gtk.Widget Root => _area;

    public int Count => _buckets.Length;

    public void SetBuckets(WinoCoverageBucket[] buckets)
    {
        _buckets = buckets;
        _hovered = -1;
        _area.SetTooltipText(null);
        _area.QueueDraw();
    }

    public void Dispose()
    {
        _self.Value = null;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }

    private void Hover(double x)
    {
        var width = _area.GetWidth();
        if (_buckets.Length == 0 || width <= 0) return;
        var index = Math.Clamp((int)(x / width * _buckets.Length), 0, _buckets.Length - 1);
        if (index == _hovered) return;
        _hovered = index;
        _area.SetTooltipText(_buckets[index].ToolTip);
    }

    private void Draw(Cairo.Context cr, int width, int height)
    {
        if (_buckets.Length == 0) return;
        var dark = Adw.StyleManager.GetDefault().GetDark();
        var indexed = WinoCoverageColors.Indexed(dark);
        var toIndex = WinoCoverageColors.ToIndex();
        var outside = WinoCoverageColors.Outside(dark);
        var column = Math.Max(1, (width - Gap * (_buckets.Length - 1)) / _buckets.Length);
        for (var index = 0; index < _buckets.Length; index++)
        {
            var bucket = _buckets[index];
            var x = index * (column + Gap);
            var bottom = (double)height;
            Segment(cr, x, ref bottom, column, bucket.Indexed, indexed);
            Segment(cr, x, ref bottom, column, bucket.ToIndex, toIndex);
            Segment(cr, x, ref bottom, column, bucket.Outside, outside);
        }
    }

    private static void Segment(Cairo.Context cr, double x, ref double bottom, double width, double height, (double R, double G, double B, double A) color)
    {
        if (height <= 0) return;
        height = Math.Min(height, bottom);
        cr.SetSourceRgba(color.R, color.G, color.B, color.A);
        cr.Rectangle(x, bottom - height, width, height);
        cr.Fill();
        bottom -= height;
    }

    private sealed class Box(WinoCoverageHistogram value)
    {
        public WinoCoverageHistogram? Value { get; set; } = value;
    }
}
