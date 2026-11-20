namespace Wino.Presentation.Linux;

public static class GtkSignals
{
    /// <summary>Calls <paramref name="changed"/> when <paramref name="property"/> of <paramref name="source"/> changes; dispose disconnects.</summary>
    public static IDisposable OnNotify(GObject.Object source, string property, Action changed)
    {
        GObject.SignalHandler<GObject.Object, GObject.Object.NotifySignalArgs> handler = (_, _) => changed();
        GObject.Object.NotifySignal.Connect(source, handler, false, property);
        return new ActionDisposable(() => GObject.Object.NotifySignal.Disconnect(source, handler));
    }
}
