#if DEBUG
namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-calendar</c> <c>s</c>: <c>"&lt;verb&gt; [args…] [&gt; /absolute/path]"</c>. The calendar page and its parts add
/// verbs while they are active (<see cref="AddVerb"/>); the result is logged and, with <c>&gt; path</c>, written to the
/// file. An unknown verb answers with the list of verbs.
/// </summary>
internal static class CalendarDebugActions
{
    private static readonly Dictionary<string, Func<string[], Task<string>>> Verbs = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-calendar", GLib.VariantType.New("s"));
        action.OnActivate += (_, args) => Run(args.Parameter?.GetString(out var _) ?? string.Empty);
        application.AddAction(action);
    }

    /// <summary>Adds a verb until the returned handle is disposed; a later registration of the same name wins.</summary>
    public static IDisposable AddVerb(string name, Func<string[], Task<string>> run)
    {
        lock (Verbs) Verbs[name] = run;
        return new Wino.Presentation.Linux.ActionDisposable(() =>
        {
            lock (Verbs)
            {
                if (Verbs.TryGetValue(name, out var current) && ReferenceEquals(current, run)) Verbs.Remove(name);
            }
        });
    }

    public static IDisposable AddVerb(string name, Func<string[], string> run) => AddVerb(name, args => Task.FromResult(run(args)));

    private static async void Run(string command)
    {
        string? path = null;
        var redirect = command.LastIndexOf('>');
        if (redirect >= 0)
        {
            path = command[(redirect + 1)..].Trim();
            command = command[..redirect];
        }
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts.Length > 0 ? parts[0] : string.Empty;
        string result;
        try
        {
            Func<string[], Task<string>>? run;
            lock (Verbs) Verbs.TryGetValue(verb, out run);
            if (run is null)
            {
                lock (Verbs) result = "verbs: " + string.Join(", ", Verbs.Keys.Order(StringComparer.Ordinal));
            }
            else result = await run(parts[1..]);
        }
        catch (Exception exception)
        {
            result = "error: " + exception.Message;
            Serilog.Log.Error(exception, "Debug action: calendar {Verb} failed.", verb);
        }
        Serilog.Log.Information("Debug action: calendar {Verb} -> {Result}", verb, result);
        if (string.IsNullOrEmpty(path)) return;
        try { DebugActions.WriteText(path, result); }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action: calendar result could not be written."); }
    }
}
#endif
