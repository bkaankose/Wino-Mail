using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Shell;

/// <summary>The grip at the inline end of a pane. It reports the pane width the pointer asks for; the owner clamps it.</summary>
internal sealed class PaneResizeHandle : IDisposable
{
    private readonly Gtk.Widget _measureFrom;
    private readonly Gtk.GestureDrag _gesture = Gtk.GestureDrag.New();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Adw.Bin _root = Adw.Bin.New();
    private double _origin;
    private bool _disposed;

    public PaneResizeHandle(Gtk.Widget measureFrom, string accessibleLabel)
    {
        _measureFrom = measureFrom;
        _root.SetSizeRequest(ShellMetrics.ResizeHandleWidth, -1);
        _root.SetHalign(Gtk.Align.End);
        _root.SetVexpand(true);
        _root.SetCursorFromName("col-resize");
        _root.AddCssClass("wino-pane-resize-handle");
        _root.AccessibleRole = Gtk.AccessibleRole.Separator;
        WinoA11y.Label(_root, accessibleLabel);

        var gesture = _gesture;
        _subscriptions.Add(GtkSignals.Connect<Gtk.GestureDrag, Gtk.GestureDrag.DragBeginSignalArgs>(
            h => gesture.OnDragBegin += h, h => gesture.OnDragBegin -= h, (_, args) =>
            {
                _origin = _root.ComputeBounds(_measureFrom, out var bounds) ? bounds.GetX() + args.StartX : args.StartX;
                gesture.SetState(Gtk.EventSequenceState.Claimed);
            }));
        _subscriptions.Add(GtkSignals.Connect<Gtk.GestureDrag, Gtk.GestureDrag.DragUpdateSignalArgs>(
            h => gesture.OnDragUpdate += h, h => gesture.OnDragUpdate -= h, (_, args) => Update(args.OffsetX)));
        _subscriptions.Add(GtkSignals.Connect<Gtk.GestureDrag, Gtk.GestureDrag.DragEndSignalArgs>(
            h => gesture.OnDragEnd += h, h => gesture.OnDragEnd -= h, (_, _) => DragEnded?.Invoke()));
        _root.AddController(gesture);
    }

    public Gtk.Widget Root => _root;
    public event Action<int>? Dragged;
    public event Action? DragEnded;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _root.RemoveController(_gesture);
        Dragged = null;
        DragEnded = null;
    }

    private void Update(double offsetX)
    {
        // The handle moves with the pane while it is dragged, so the pointer is measured from the split's edge as it was
        // when the drag began; the handle's current coordinates would feed back and jitter.
        var pointer = _origin + offsetX;
        var width = _root.GetDirection() == Gtk.TextDirection.Rtl ? _measureFrom.GetWidth() - pointer : pointer;
        Dragged?.Invoke((int)Math.Round(width));
    }
}
