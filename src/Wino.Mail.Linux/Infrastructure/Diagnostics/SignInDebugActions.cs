#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Platform.Linux.Authentication;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Browser sign-in checks: <c>debug-signin-oauth 'url'</c> and <c>debug-signin-wino 'url'</c> run the OAuth and the Wino
/// Account broker and log the callback's path and parameter names (never values); <c>debug-signin-cancel</c> cancels the
/// running call; <c>debug-signin-presenter ''|'fail'</c> shows the waiting dialog for a fake request (fail: browser launch
/// failed after 1 s); <c>debug-signin-close</c> closes it; <c>debug-signin-state 'path'</c> writes the dialog and callback state.
/// <c>WINO_DEBUG_SIGNIN_TIMEOUT_SECONDS</c> limits a debug call (default 5 minutes).
/// </summary>
internal static class SignInDebugActions
{
    private static CancellationTokenSource? _current;
    private static IExternalBrowserAuthenticationSession? _presenterSession;

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-signin-oauth", true, value => Run("oauth", services, (provider, uri, token) =>
            provider.GetRequiredService<IOAuthBrowserBroker>().AuthorizeAsync(uri, token), value));
        Add(application, "debug-signin-wino", true, value => Run("wino", services, (provider, uri, token) =>
            provider.GetRequiredService<IWinoAccountBrowserAuthBroker>().AuthorizeAsync(uri, "wino", token), value));
        Add(application, "debug-signin-cancel", false, _ => _current?.Cancel());
        Add(application, "debug-signin-presenter", true, value => Observe(PresentAsync(services, value)));
        Add(application, "debug-signin-close", false, _ => Observe(ClosePresenterAsync()));
        Add(application, "debug-signin-state", true, value => WriteState(services, value));
    }

    /// <summary>A token for a debug call: <c>WINO_DEBUG_SIGNIN_TIMEOUT_SECONDS</c>, else five minutes.</summary>
    internal static CancellationTokenSource NewCall()
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("WINO_DEBUG_SIGNIN_TIMEOUT_SECONDS"), out var value) && value > 0 ? value : 300;
        var source = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        _current = source;
        return source;
    }

    internal static void CancelCurrent() => _current?.Cancel();

    internal static string Names(Uri uri)
        => string.Join(',', uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => Uri.UnescapeDataString(pair.Split('=')[0])));

    internal static void LogFailure(string name, Exception exception)
    {
        var kind = exception is OAuthSignInException oauth ? oauth.Kind.ToString() : "-";
        Serilog.Log.Information("Debug action: {Name} failed type={Type} kind={Kind}", name, exception.GetType().Name, kind);
    }

    private static void Add(Adw.Application application, string name, bool parameter, Action<string> run)
    {
        var action = Gio.SimpleAction.New(name, parameter ? GLib.VariantType.New("s") : null);
        action.OnActivate += (sender, args) =>
        {
            try { run(args.Parameter?.GetString(out _) ?? string.Empty); }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
        };
        application.AddAction(action);
    }

    private static void Run(string name, Func<IServiceProvider?> services, Func<IServiceProvider, Uri, CancellationToken, Task<Uri>> call, string value)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var uri = new Uri(value);
        var source = NewCall();
        Observe(RunAsync());

        async Task RunAsync()
        {
            try
            {
                var callback = await Task.Run(() => call(provider, uri, source.Token));
                Serilog.Log.Information("Debug action: {Name} broker completed path={Path} params={Params}", name, callback.AbsolutePath, Names(callback));
            }
            catch (Exception exception) { LogFailure(name + " broker", exception); }
            finally
            {
                if (ReferenceEquals(_current, source)) _current = null;
                source.Dispose();
            }
        }
    }

    private static async Task PresentAsync(Func<IServiceProvider?> services, string mode)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        await ClosePresenterAsync();
        var presenter = provider.GetRequiredService<IExternalBrowserAuthenticationPresenter>();
        var session = await presenter.ShowAsync(new ExternalBrowserAuthenticationRequest("Example", new Uri("https://example.invalid/authorize?client_id=x&state=debug")),
            () => Serilog.Log.Information("Debug action: sign-in dialog cancel requested."));
        _presenterSession = session;
        if (mode == "fail")
        {
            await Task.Delay(1000);
            await session.NotifyBrowserLaunchFailedAsync();
        }
    }

    private static async Task ClosePresenterAsync()
    {
        var session = _presenterSession;
        _presenterSession = null;
        if (session is not null) await session.DisposeAsync();
    }

    private static void WriteState(Func<IServiceProvider?> services, string path)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var dialog = provider.GetRequiredService<ExternalBrowserAuthPresenter>().Current;
        var callbacks = provider.GetRequiredService<LinuxExternalAuthCallbacks>();
        DebugActions.WriteText(path, $"dialog={(dialog is null ? "none" : "open")} launchFailed={dialog?.IsBrowserLaunchFailed ?? false} copied={dialog?.IsLinkCopied ?? false} winoWaiting={callbacks.IsWaiting}\n");
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "A sign-in debug action failed."); }
    }
}
#endif
