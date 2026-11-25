#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-embedded-signin ['start', 'redirect']</c> runs the embedded browser broker and logs the status and the
/// redirect's path and parameter names (never values); <c>debug-embedded-cancel</c> cancels it through its token;
/// <c>debug-embedded-state 'path'</c> writes whether a sign-in window is open, its page host and its page title.
/// </summary>
internal static class EmbeddedSignInDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var start = Gio.SimpleAction.New("debug-embedded-signin", GLib.VariantType.New("as"));
        start.OnActivate += (sender, args) =>
        {
            var parts = args.Parameter?.GetStrv(out _) ?? [];
            if (services() is not { } provider || parts.Length < 2) return;
            Observe(RunAsync(provider, new Uri(parts[0]), new Uri(parts[1])));
        };
        application.AddAction(start);

        var cancel = Gio.SimpleAction.New("debug-embedded-cancel", null);
        cancel.OnActivate += (sender, args) => SignInDebugActions.CancelCurrent();
        application.AddAction(cancel);

        var state = Gio.SimpleAction.New("debug-embedded-state", GLib.VariantType.New("s"));
        state.OnActivate += (sender, args) =>
        {
            if (services() is not { } provider) return;
            var window = provider.GetRequiredService<GtkEmbeddedBrowserAuthBroker>().Current;
            DebugActions.WriteText(args.Parameter?.GetString(out _) ?? string.Empty,
                $"window={(window is null ? "none" : "open")} host={window?.CurrentHost} title={window?.Title}\n");
        };
        application.AddAction(state);
    }

    private static async Task RunAsync(IServiceProvider provider, Uri start, Uri redirect)
    {
        using var source = SignInDebugActions.NewCall();
        try
        {
            var result = await Task.Run(() => provider.GetRequiredService<IEmbeddedBrowserAuthBroker>().AuthenticateAsync(start, redirect, source.Token));
            Serilog.Log.Information("Debug action: embedded sign-in status={Status} path={Path} params={Params}", result.Status,
                result.RedirectUri?.AbsolutePath ?? "-", result.RedirectUri is { } uri ? SignInDebugActions.Names(uri) : "-");
        }
        catch (Exception exception) { SignInDebugActions.LogFailure("embedded sign-in", exception); }
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-embedded-signin failed."); }
    }
}
#endif
