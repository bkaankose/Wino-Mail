#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Activation;
using Wino.Mail.Linux.Views.Settings.WinoAccount;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-winoaccount</c> (<c>as</c>): <c>['open']</c> / <c>['open', 'checkout']</c> opens Settings › Wino Account
/// (the second as the billing return does), <c>['tile', i]</c> selects an offer tile, <c>['reveal-detail']</c> scrolls to the offer detail, <c>['state', path]</c> writes the
/// page state.
/// </summary>
internal static class WinoAccountPageDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-winoaccount", GLib.VariantType.New("as"));
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
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var op = parts.Length > 0 ? parts[0] : string.Empty;
        var value = parts.Length > 1 ? parts[1] : string.Empty;
        switch (op)
        {
            case "open":
                await provider.GetRequiredService<IApplicationShellHost>().ShowSettingsAsync(WinoPage.WinoAccountManagementPage,
                    value == "checkout" ? WinoAccountManagementActivationReason.CheckoutCompleted : null);
                Serilog.Log.Information("Debug action: winoaccount page opened ({Reason}).", value == "checkout" ? "checkout" : "plain");
                break;
            case "tile" when int.TryParse(value, out var index):
                await dispatcher.ExecuteOnUIThread(() => WinoAccountPage.Current?.DebugSelectTile(index));
                break;
            case "reveal-detail":
                await dispatcher.ExecuteOnUIThread(() => WinoAccountPage.Current?.DebugRevealDetail());
                break;
            case "state":
            {
                var text = string.Empty;
                await dispatcher.ExecuteOnUIThread(() => text = WinoAccountPage.Current?.Describe() ?? "no page");
                await File.WriteAllTextAsync(value, text + Environment.NewLine);
                Serilog.Log.Information("Debug action: winoaccount state written to {Path}.", value);
                break;
            }
            default:
                throw new ArgumentException($"Unknown debug-winoaccount operation '{op}'.");
        }
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-winoaccount failed."); }
    }
}
#endif
