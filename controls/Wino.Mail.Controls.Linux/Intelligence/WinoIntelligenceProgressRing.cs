using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Intelligence;

/// <summary>
/// The indeterminate Wino Intelligence ring: a faint track and a rotating arc in the brand gradient. GTK cannot animate a
/// conic gradient in CSS, so it is drawn with Cairo on the frame clock, and the tick callback runs only while the ring is
/// <see cref="Active"/> and mapped.
/// </summary>
public sealed class WinoIntelligenceProgressRing : IDisposable
{
    private const double SweepRadians = Math.PI * 0.6;
    private const double TurnMicroseconds = 1_000_000;

    private readonly Gtk.DrawingArea _area = Gtk.DrawingArea.New();
    private readonly Box _self;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly double _lineWidth;
    private uint _tick;
    private long _startUs;
    private double _angle;
    private bool _active;

    public WinoIntelligenceProgressRing(int size = 24, double lineWidth = 0)
    {
        WinoLiveObjects.Track(this);
        _self = new Box(this);
        var self = _self;
        _lineWidth = lineWidth > 0 ? lineWidth : Math.Max(2, size / 9.0);
        _area.SetContentWidth(size);
        _area.SetContentHeight(size);
        _area.SetHalign(Gtk.Align.Center);
        _area.SetValign(Gtk.Align.Center);
        _area.SetDrawFunc((_, cr, width, height) => self.Value?.Draw(cr, width, height));
        _subscriptions.Add(GtkSignals.Connect<Gtk.Widget>(h => _area.OnMap += h, h => _area.OnMap -= h, (_, _) => self.Value?.UpdateTick()));
        _subscriptions.Add(GtkSignals.Connect<Gtk.Widget>(h => _area.OnUnmap += h, h => _area.OnUnmap -= h, (_, _) => self.Value?.StopTick()));
        _subscriptions.Add(GtkSignals.OnNotify(Adw.StyleManager.GetDefault(), "dark", () => self.Value?._area.QueueDraw()));
    }

    public Gtk.Widget Root => _area;

    /// <summary>Whether the arc turns; an inactive ring draws nothing.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            UpdateTick();
            _area.QueueDraw();
        }
    }

    /// <summary>True while a tick callback is installed, for leak checks.</summary>
    public bool IsTicking => _tick != 0;

    public void Dispose()
    {
        StopTick();
        _self.Value = null;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }

    private void UpdateTick()
    {
        if (!_active || !_area.GetMapped())
        {
            StopTick();
            return;
        }
        if (_tick != 0) return;
        var self = _self;
        _startUs = 0;
        _tick = _area.AddTickCallback((_, clock) =>
        {
            if (self.Value is not { } ring) return false;
            var now = clock.GetFrameTime();
            if (ring._startUs == 0) ring._startUs = now;
            ring._angle = (now - ring._startUs) % TurnMicroseconds / TurnMicroseconds * 2 * Math.PI;
            ring._area.QueueDraw();
            return true;
        });
    }

    private void StopTick()
    {
        if (_tick == 0) return;
        _area.RemoveTickCallback(_tick);
        _tick = 0;
    }

    private void Draw(Cairo.Context cr, int width, int height)
    {
        if (!_active) return;
        var dark = IntelligencePaint.IsDark;
        var radius = Math.Min(width, height) / 2.0 - _lineWidth / 2;
        var cx = width / 2.0;
        var cy = height / 2.0;
        cr.LineWidth = _lineWidth;
        cr.NewPath();
        cr.Arc(cx, cy, radius, 0, 2 * Math.PI);
        var (r, g, b) = IntelligencePaint.Solid(dark);
        cr.SetSourceRgba(r, g, b, 0.18);
        cr.Stroke();
        cr.NewPath();
        cr.Arc(cx, cy, radius, _angle - Math.PI / 2, _angle - Math.PI / 2 + SweepRadians);
        IntelligencePaint.SetGradient(cr, cx - radius, cy - radius, radius * 2, radius * 2, dark);
        cr.LineCap = Cairo.LineCap.Round;
        cr.Stroke();
    }

    private sealed class Box(WinoIntelligenceProgressRing value)
    {
        public WinoIntelligenceProgressRing? Value { get; set; } = value;
    }
}
