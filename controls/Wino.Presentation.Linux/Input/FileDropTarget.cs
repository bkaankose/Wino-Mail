using Wino.Presentation.Linux.Native;

namespace Wino.Presentation.Linux.Input;

/// <summary>
/// A file drop on a widget (Nautilus and portal drags deliver a GdkFileList). In the capture phase an ancestor sees the
/// drag before a web view below it, which would otherwise take the drop. Dispose removes the controller.
/// </summary>
public sealed class FileDropTarget : IDisposable
{
    private readonly Gtk.Widget _widget;
    private readonly Gtk.DropTarget _target;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _disposed;

    private FileDropTarget(Gtk.Widget widget, bool capture)
    {
        _widget = widget;
        _target = Gtk.DropTarget.New(Gdk.FileList.GetGType(), Gdk.DragAction.Copy);
        if (capture) _target.SetPropagationPhase(Gtk.PropagationPhase.Capture);
        _subscriptions.Add(GtkSignals.ConnectReturning<Gtk.DropTarget, Gtk.DropTarget.EnterSignalArgs, Gdk.DragAction>(
            h => _target.OnEnter += h, h => _target.OnEnter -= h, (_, args) =>
            {
                if (!Accepts) return 0;
                Entered?.Invoke(this, args.X, args.Y);
                return Gdk.DragAction.Copy;
            }, 0));
        _subscriptions.Add(GtkSignals.ConnectReturning<Gtk.DropTarget, Gtk.DropTarget.MotionSignalArgs, Gdk.DragAction>(
            h => _target.OnMotion += h, h => _target.OnMotion -= h, (_, args) =>
            {
                if (!Accepts) return 0;
                Moved?.Invoke(this, args.X, args.Y);
                return Gdk.DragAction.Copy;
            }, 0));
        _subscriptions.Add(GtkSignals.Connect<Gtk.DropTarget>(h => _target.OnLeave += h, h => _target.OnLeave -= h, (_, _) => Left?.Invoke(this)));
        _subscriptions.Add(GtkSignals.ConnectReturning<Gtk.DropTarget, Gtk.DropTarget.DropSignalArgs, bool>(
            h => _target.OnDrop += h, h => _target.OnDrop -= h, (_, args) => Drop(args.Value, args.X, args.Y), false));
        widget.AddController(_target);
    }

    /// <summary>Attaches a file drop target to <paramref name="widget"/>; <paramref name="capture"/> sees the drag before the widget's children.</summary>
    public static FileDropTarget Attach(Gtk.Widget widget, bool capture) => new(widget, capture);

    public Func<bool>? CanAccept { get; set; }

    public event Action<FileDropTarget, double, double>? Entered;
    public event Action<FileDropTarget, double, double>? Moved;
    public event Action<FileDropTarget>? Left;
    public event Action<FileDropTarget, string[], double, double>? Dropped;

    private bool Accepts => !_disposed && (CanAccept?.Invoke() ?? true);

    /// <summary>The drop handler's body: the value holds the dragged GdkFileList. Debug checks call it with a list they built.</summary>
    public bool Drop(GObject.Value value, double x, double y)
    {
        if (!Accepts) return false;
        var paths = GdkFileLists.GetPaths(value);
        if (paths.Length > 0) Dropped?.Invoke(this, paths, x, y);
        Left?.Invoke(this);
        return paths.Length > 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        _widget.RemoveController(_target);
        Entered = null;
        Moved = null;
        Left = null;
        Dropped = null;
    }
}
