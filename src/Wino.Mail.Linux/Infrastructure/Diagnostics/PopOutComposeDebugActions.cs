#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Views.Mail.Compose;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-compose-window</c> (<c>as</c>): <c>['pop-out']</c> and <c>['dock']</c> on the active composer (<c>debug-compose</c>'s
/// <see cref="ComposeDebugActions.Current"/>), <c>['close']</c> closes its window as the app does, <c>['list']</c> logs the composer
/// windows and live composers, <c>['select', key|'pane']</c> makes the composer in that window (or in the pane) the active one for
/// <c>debug-compose</c>, <c>['focus', key]</c> presents that window.
/// </summary>
internal static class PopOutComposeDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-compose-window", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => _ = RunAsync(services, args.Parameter?.GetStrv(out _) ?? []);
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] arguments)
    {
        try
        {
            var provider = services() ?? throw new InvalidOperationException("The services are not ready.");
            var windows = provider.GetRequiredService<WindowManager>();
            var command = Argument(arguments, 0);
            switch (command)
            {
                case "pop-out": Log($"pop-out={await Current().PopOutAsync()}"); return;
                case "dock": Log($"dock={await Current().DockAsync()}"); return;
                case "close": (Current().PopOutWindow ?? throw new InvalidOperationException("The composer is not popped out.")).Close(); break;
                case "list":
                {
                    var composers = WinoLiveObjects.Instances(nameof(ComposeView)).OfType<ComposeView>()
                        .Select(static view => $"{(view.PopOutWindowKey ?? "pane")}:{view.ViewModel.Subject}");
                    Log($"windows=[{string.Join(" | ", windows.Describe().Where(static line => line.StartsWith(ComposeView.WindowKind + ":", StringComparison.Ordinal)))}] " +
                        $"composers=[{string.Join(" | ", composers)}] secondary={WinoLiveObjects.Instances(nameof(Views.SecondaryWindow)).Length}");
                    return;
                }
                case "select":
                {
                    var key = Argument(arguments, 1);
                    var view = WinoLiveObjects.Instances(nameof(ComposeView)).OfType<ComposeView>()
                        .FirstOrDefault(view => key == "pane" ? view.PopOutWindowKey is null : view.PopOutWindowKey == key)
                        ?? throw new InvalidOperationException($"No composer for '{key}'.");
                    ComposeDebugActions.SetCurrent(view);
                    break;
                }
                case "focus":
                    if (!windows.TryPresent(Argument(arguments, 1))) throw new InvalidOperationException("No such window.");
                    break;
                default: throw new ArgumentException($"'{command}' is not a debug-compose-window command.");
            }
            Log("done " + string.Join(' ', arguments));
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-compose-window failed."); }
    }

    private static ComposeView Current() => ComposeDebugActions.Current ?? throw new InvalidOperationException("No composer is shown.");

    private static void Log(string line) => Serilog.Log.Information("compose-window-debug: {Line}", line);

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");
}
#endif
