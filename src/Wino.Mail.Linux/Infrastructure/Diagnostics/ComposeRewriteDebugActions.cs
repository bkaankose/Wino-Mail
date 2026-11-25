#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-compose-rewrite</c> (<c>as</c>) on the current composer: <c>['state', path]</c> writes the rewrite session and
/// strip; <c>['run', mode]</c> starts a real rewrite with that tone, as the menu does (without an entitlement the session
/// refuses and the strip shows its error). No fake session and no forced availability.
/// </summary>
internal static class ComposeRewriteDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-compose-rewrite", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) =>
        {
            var parts = args.Parameter?.GetStrv(out _) ?? [];
            Observe(RunAsync(services, parts));
        };
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] parts)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var op = parts.Length > 0 ? parts[0] : string.Empty;
        var value = parts.Length > 1 ? parts[1] : string.Empty;
        await provider.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() =>
        {
            var view = ComposeDebugActions.Current ?? throw new InvalidOperationException("No composer is open.");
            switch (op)
            {
                case "state": DebugActions.WriteText(value, view.DescribeRewrite()); break;
                case "run": view.DebugStartRewrite(value); break;
                default: throw new ArgumentException($"Unknown debug-compose-rewrite operation '{op}'.");
            }
        });
        Serilog.Log.Information("Debug action: compose rewrite {Operation} {Value} done.", op, value);
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-compose-rewrite failed."); }
    }
}
#endif
