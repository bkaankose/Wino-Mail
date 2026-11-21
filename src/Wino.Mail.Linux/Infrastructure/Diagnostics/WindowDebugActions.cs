#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Views;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-window</c> <c>as</c>: <c>['open', WinoPage]</c> shows the page's view in a secondary window keyed
/// <c>debug:&lt;page&gt;</c>, <c>['secondary', WinoPage]</c> goes through the onboarding presenter, <c>['list', path]</c>,
/// <c>['close', key]</c>, <c>['resize', key, width, height]</c>, <c>['hide-all']</c>, <c>['show-all']</c>.
/// </summary>
internal static class WindowDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-window", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) =>
        {
            try
            {
                var arguments = args.Parameter?.GetStrv(out var _) ?? [];
                if (arguments.Length == 0) throw new ArgumentException("An operation is required.");
                Run(application, services() ?? throw new InvalidOperationException("The services are not ready."), arguments);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-window failed."); }
        };
        application.AddAction(action);
    }

    private static void Run(Adw.Application application, IServiceProvider services, string[] arguments)
    {
        var windows = services.GetRequiredService<WindowManager>();
        switch (arguments[0])
        {
            case "open":
            {
                var page = Page(Argument(arguments, 1));
                if (windows.TryPresent("debug:" + page)) return;
                if (!services.GetRequiredService<LinuxPageRegistry>().TryGet(page, null, out var route)) throw new ArgumentException($"{page} has no route.");
                var view = route.Create(services);
                var window = new SecondaryWindow(application, "debug", view.Root, page.ToString(), 640, 480, windows.Sizes);
                window.Closing = view.ReleaseAsync;
                windows.Add("debug:" + page, window);
                window.Present();
                _ = ActivateAsync(view);
                Report("open", "debug:" + page);
                return;
            }
            case "secondary":
            {
                var page = Page(Argument(arguments, 1));
                _ = ShowSecondaryAsync(services.GetRequiredService<GtkNavigationService>(), page);
                return;
            }
            case "list":
                DebugActions.WriteText(Argument(arguments, 1), string.Join('\n', windows.Describe()) + "\n");
                Report("list", $"{windows.Count} windows");
                return;
            case "close":
                (windows.Find(Argument(arguments, 1)) ?? throw new ArgumentException("No such window.")).Close();
                Report("close", arguments[1]);
                return;
            case "resize":
            {
                var window = windows.Find(Argument(arguments, 1)) ?? throw new ArgumentException("No such window.");
                if (window.Window.IsMaximized()) window.Window.Unmaximize();
                window.Window.SetDefaultSize(int.Parse(Argument(arguments, 2)), int.Parse(Argument(arguments, 3)));
                Report("resize", $"{arguments[1]} {arguments[2]}x{arguments[3]}");
                return;
            }
            case "hide-all":
                windows.SetHidden(true);
                Report("hide-all", string.Join(", ", windows.Describe()));
                return;
            case "show-all":
                windows.SetHidden(false);
                Report("show-all", string.Join(", ", windows.Describe()));
                return;
            default:
                throw new ArgumentException($"Unknown operation '{arguments[0]}'.");
        }
    }

    private static async Task ActivateAsync(IWinoView view)
    {
        try { await view.ActivateAsync(NavigationMode.New, null); }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-window: the page did not activate."); }
    }

    private static async Task ShowSecondaryAsync(GtkNavigationService navigation, WinoPage page)
    {
        try
        {
            await navigation.ShowInSecondaryWindowAsync(page, null, NavigationMode.New, static () => { });
            Report("secondary", page.ToString());
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-window secondary failed."); }
    }

    private static WinoPage Page(string name)
        => Enum.TryParse<WinoPage>(name, true, out var page) ? page : throw new ArgumentException($"Unknown page '{name}'.");

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length && !string.IsNullOrWhiteSpace(arguments[index]) ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");

    private static void Report(string op, string result) => Serilog.Log.Information("Debug action: window {Op} -> {Result}", op, result);
}
#endif
