namespace Wino.Presentation.Linux;

/// <summary>
/// Signal subscriptions that cannot keep their targets alive. Gir.Core 0.8.1 keeps a handler in the object's closure
/// table after <c>-=</c>, so a handler that captured a view roots it for as long as the GObject lives. Every helper here
/// connects a forwarder that reaches the handler through a box; disposing the subscription empties the box and then
/// disconnects. Handlers may capture anything.
/// </summary>
public static class GtkSignals
{
    /// <summary>
    /// Connects <paramref name="handler"/> through <paramref name="connect"/> (<c>h =&gt; widget.OnSomething += h</c>);
    /// dispose releases it and calls <paramref name="disconnect"/> (<c>h =&gt; widget.OnSomething -= h</c>).
    /// </summary>
    public static IDisposable Connect<TSender>(
        Action<GObject.SignalHandler<TSender>> connect,
        Action<GObject.SignalHandler<TSender>> disconnect,
        GObject.SignalHandler<TSender> handler)
        where TSender : GObject.NativeObject
    {
        var box = new Box<GObject.SignalHandler<TSender>>(handler);
        GObject.SignalHandler<TSender> forward = (sender, args) => box.Value?.Invoke(sender, args);
        connect(forward);
        return new Subscription<GObject.SignalHandler<TSender>>(box, forward, disconnect);
    }

    /// <inheritdoc cref="Connect{TSender}"/>
    public static IDisposable Connect<TSender, TArgs>(
        Action<GObject.SignalHandler<TSender, TArgs>> connect,
        Action<GObject.SignalHandler<TSender, TArgs>> disconnect,
        GObject.SignalHandler<TSender, TArgs> handler)
        where TSender : GObject.NativeObject
        where TArgs : GObject.SignalArgs
    {
        var box = new Box<GObject.SignalHandler<TSender, TArgs>>(handler);
        GObject.SignalHandler<TSender, TArgs> forward = (sender, args) => box.Value?.Invoke(sender, args);
        connect(forward);
        return new Subscription<GObject.SignalHandler<TSender, TArgs>>(box, forward, disconnect);
    }

    /// <summary>The <see cref="Connect{TSender}"/> pattern for signals whose handler returns a value; an emptied box returns the default.</summary>
    public static IDisposable Connect<TSender, TArgs, TReturn>(
        Action<GObject.ReturningSignalHandler<TSender, TArgs, TReturn>> connect,
        Action<GObject.ReturningSignalHandler<TSender, TArgs, TReturn>> disconnect,
        GObject.ReturningSignalHandler<TSender, TArgs, TReturn> handler)
        where TSender : GObject.NativeObject
        where TArgs : GObject.SignalArgs
        where TReturn : struct
    {
        var box = new Box<GObject.ReturningSignalHandler<TSender, TArgs, TReturn>>(handler);
        GObject.ReturningSignalHandler<TSender, TArgs, TReturn> forward = (sender, args) => box.Value is { } target ? target(sender, args) : default;
        connect(forward);
        return new Subscription<GObject.ReturningSignalHandler<TSender, TArgs, TReturn>>(box, forward, disconnect);
    }

    /// <summary>Calls <paramref name="changed"/> when <paramref name="property"/> of <paramref name="source"/> changes; dispose disconnects.</summary>
    public static IDisposable OnNotify(GObject.Object source, string property, Action changed)
        => Connect<GObject.Object, GObject.Object.NotifySignalArgs>(
            forward => GObject.Object.NotifySignal.Connect(source, forward, false, property),
            forward => GObject.Object.NotifySignal.Disconnect(source, forward),
            (_, _) => changed());

    public static IDisposable OnClicked(Gtk.Button button, Action clicked)
        => Connect<Gtk.Button>(h => button.OnClicked += h, h => button.OnClicked -= h, (_, _) => clicked());

    public static IDisposable OnToggled(Gtk.CheckButton toggle, Action toggled)
        => Connect<Gtk.CheckButton>(h => toggle.OnToggled += h, h => toggle.OnToggled -= h, (_, _) => toggled());

    public static IDisposable OnToggled(Gtk.ToggleButton toggle, Action toggled)
        => Connect<Gtk.ToggleButton>(h => toggle.OnToggled += h, h => toggle.OnToggled -= h, (_, _) => toggled());

    public static IDisposable OnChanged(Gtk.Editable editable, Action changed)
        => Connect<Gtk.Editable>(h => editable.OnChanged += h, h => editable.OnChanged -= h, (_, _) => changed());

    public static IDisposable OnActivate(Gio.SimpleAction action, Action<GLib.Variant?> activated)
        => Connect<Gio.SimpleAction, Gio.SimpleAction.ActivateSignalArgs>(
            h => action.OnActivate += h, h => action.OnActivate -= h, (_, args) => activated(args.Parameter));

    private sealed class Box<T>(T value) where T : class
    {
        public T? Value = value;
    }

    private sealed class Subscription<T>(Box<T> box, T forward, Action<T> disconnect) : IDisposable where T : class
    {
        private Action<T>? _disconnect = disconnect;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disconnect, null) is not { } disconnect) return;
            box.Value = null;
            disconnect(forward);
        }
    }
}
