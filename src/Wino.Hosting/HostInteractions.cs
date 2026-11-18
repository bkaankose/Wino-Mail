namespace Wino.Hosting;

/// <summary>What the backend asked a screen to do.</summary>
public enum HostInteractionKind
{
    Dialog,
    InfoBar,
    Notification,
    Navigation,
    Theme,
    Launch,
    SignIn,
    Startup
}

/// <summary>One request the backend made of the user interface, and how the host answered it.</summary>
public sealed record HostInteraction(DateTimeOffset At, HostInteractionKind Kind, string Name, string Detail, string Answer)
{
    public override string ToString()
        => $"{At:HH:mm:ss.fff} {Kind} {Name}" +
           (string.IsNullOrWhiteSpace(Detail) ? string.Empty : $" [{Detail}]") +
           (string.IsNullOrWhiteSpace(Answer) ? string.Empty : $" -> {Answer}");
}

/// <summary>The thread-safe record of every <see cref="HostInteraction"/> of one host.</summary>
public sealed class HostInteractionLog
{
    private readonly object _gate = new();
    private readonly List<HostInteraction> _items = [];

    public IReadOnlyList<HostInteraction> Items
    {
        get
        {
            lock (_gate) return _items.ToArray();
        }
    }

    public void Record(HostInteractionKind kind, string name, string? detail = null, string? answer = null)
    {
        var item = new HostInteraction(DateTimeOffset.Now, kind, name, detail ?? string.Empty, answer ?? string.Empty);
        lock (_gate) _items.Add(item);
        Serilog.Log.Information("Host interaction: {Interaction}", item.ToString());
    }
}
