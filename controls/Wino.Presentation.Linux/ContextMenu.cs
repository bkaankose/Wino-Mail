namespace Wino.Presentation.Linux;

public static class ContextMenu
{
    /// <summary>The widget action that opens an attached menu; the Menu key and Shift+F10 activate it.</summary>
    public const string PopupAction = "wino-context.popup";

    private static int _openMenus;

    /// <summary>Popovers opened through this class that are not closed and torn down yet.</summary>
    public static int OpenMenus => Volatile.Read(ref _openMenus);

    /// <summary>
    /// Opens a menu built by <paramref name="populate"/> on right click (secondary button), long press (touch) and the
    /// Menu key or Shift+F10 while focus is inside <paramref name="widget"/>. A builder left empty shows nothing.
    /// Dispose the returned attachment with the view that owns the widget.
    /// </summary>
    public static ContextMenuAttachment Attach(Gtk.Widget widget, Action<WinoMenuBuilder> populate)
        => new(widget, populate);

    /// <summary>
    /// Shows <paramref name="menu"/> in a popover on <paramref name="widget"/>, pointing at (<paramref name="x"/>,
    /// <paramref name="y"/>) in widget coordinates, or at the widget's centre when no point is given (keyboard). The popover
    /// owns the menu from here on: it is disposed and the popover removed once the popover closes.
    /// </summary>
    public static Gtk.PopoverMenu Show(Gtk.Widget widget, WinoMenu menu, double? x = null, double? y = null, Action? onClosed = null)
    {
        var popover = Gtk.PopoverMenu.NewFromModel(menu.Model);
        popover.SetHasArrow(false);
        popover.SetHalign(Gtk.Align.Start);
        popover.SetPosition(Gtk.PositionType.Bottom);
        popover.InsertActionGroup(WinoMenus.ActionPrefix, menu.Actions);
        popover.SetParent(widget);
        popover.SetPointingTo(new Gdk.Rectangle
        {
            X = (int)Math.Round(x ?? widget.GetWidth() / 2.0),
            Y = (int)Math.Round(y ?? widget.GetHeight() / 2.0),
            Width = 1,
            Height = 1,
        });

        Interlocked.Increment(ref _openMenus);
        _ = new Session(popover, menu, onClosed);
        popover.Popup();
        return popover;
    }

    private sealed class Session
    {
        private readonly Gtk.PopoverMenu _popover;
        private readonly WinoMenu _menu;
        private readonly Action? _onClosed;
        private readonly IDisposable _closed;

        public Session(Gtk.PopoverMenu popover, WinoMenu menu, Action? onClosed)
        {
            _popover = popover;
            _menu = menu;
            _onClosed = onClosed;
            _closed = GtkSignals.Connect<Gtk.Popover>(h => popover.OnClosed += h, h => popover.OnClosed -= h, (_, _) => OnClosed());
        }

        private void OnClosed()
        {
            _closed.Dispose();
            _onClosed?.Invoke();
            // GtkModelButton pops the menu down before it activates the item's action, so the actions must outlive
            // the closed signal; teardown waits for the next main-loop turn.
            MainLoop.Post(() =>
            {
                _popover.InsertActionGroup(WinoMenus.ActionPrefix, null);
                _popover.Unparent();
                _menu.Dispose();
                Interlocked.Decrement(ref _openMenus);
            });
        }
    }
}

public sealed class ContextMenuAttachment : IDisposable
{
    private readonly Gtk.Widget _widget;
    private readonly Action<WinoMenuBuilder> _populate;
    private readonly Gtk.GestureClick _click = Gtk.GestureClick.New();
    private readonly Gtk.GestureLongPress _longPress = Gtk.GestureLongPress.New();
    private readonly Gtk.ShortcutController _keys = Gtk.ShortcutController.New();
    private readonly Gio.SimpleActionGroup _actions = Gio.SimpleActionGroup.New();
    private readonly Gio.SimpleAction _popup = Gio.SimpleAction.New("popup", null);
    private readonly IDisposable _onClick;
    private readonly IDisposable _onLongPress;
    private readonly IDisposable _onPopup;
    private Gtk.PopoverMenu? _open;
    private bool _disposed;

    internal ContextMenuAttachment(Gtk.Widget widget, Action<WinoMenuBuilder> populate)
    {
        _widget = widget;
        _populate = populate;

        _onClick = GtkSignals.Connect<Gtk.GestureClick, Gtk.GestureClick.PressedSignalArgs>(h => _click.OnPressed += h, h => _click.OnPressed -= h, (gesture, args) =>
        {
            if (args.NPress != 1) return;
            gesture.SetState(Gtk.EventSequenceState.Claimed);
            Open(args.X, args.Y);
        });
        _click.SetButton((uint)Gdk.Constants.BUTTON_SECONDARY);
        widget.AddController(_click);

        _onLongPress = GtkSignals.Connect<Gtk.GestureLongPress, Gtk.GestureLongPress.PressedSignalArgs>(h => _longPress.OnPressed += h, h => _longPress.OnPressed -= h, (gesture, args) =>
        {
            gesture.SetState(Gtk.EventSequenceState.Claimed);
            Open(args.X, args.Y);
        });
        _longPress.SetTouchOnly(true);
        widget.AddController(_longPress);

        _onPopup = GtkSignals.OnActivate(_popup, _ => Open(null, null));
        _actions.AddAction(_popup);
        widget.InsertActionGroup("wino-context", _actions);
        _keys.AddShortcut(Gtk.Shortcut.New(Gtk.ShortcutTrigger.ParseString("Menu|<Shift>F10"), Gtk.NamedAction.New(ContextMenu.PopupAction)));
        widget.AddController(_keys);
    }

    /// <summary>The menu popover that is open now, or null.</summary>
    public Gtk.PopoverMenu? Current => _open;

    /// <summary>Builds the menu and shows it; null when the builder added nothing. A menu that is still open closes first.</summary>
    public Gtk.PopoverMenu? Open(double? x = null, double? y = null)
    {
        if (_disposed) return null;
        _open?.Popdown();
        var builder = WinoMenus.Create();
        _populate(builder);
        var menu = builder.Build();
        if (menu.IsEmpty)
        {
            menu.Dispose();
            return null;
        }
        Gtk.PopoverMenu? popover = null;
        popover = ContextMenu.Show(_widget, menu, x, y, () =>
        {
            if (ReferenceEquals(_open, popover)) _open = null;
        });
        _open = popover;
        return popover;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _open?.Popdown();
        _onClick.Dispose();
        _onLongPress.Dispose();
        _onPopup.Dispose();
        _widget.RemoveController(_click);
        _widget.RemoveController(_longPress);
        _widget.RemoveController(_keys);
        _widget.InsertActionGroup("wino-context", null);
    }
}
