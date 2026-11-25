#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-briefing</c> (<c>as</c>): <c>['force-available', 'on'|'off']</c> shows the header button without an
/// entitlement (the panel then shows the ViewModel's real unavailable state); <c>['open']</c> / <c>['close']</c> toggle the
/// panel as the header button does; <c>['state', path]</c>; <c>['date', i]</c> picks a day through the pager;
/// <c>['primary', i]</c> / <c>['ignore', i]</c> press a card's buttons.
/// </summary>
internal static class BriefingDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-briefing", GLib.VariantType.New("as"));
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
        var presenter = provider.GetRequiredService<DailyBriefingPresenter>();
        var op = parts.Length > 0 ? parts[0] : string.Empty;
        var value = parts.Length > 1 ? parts[1] : string.Empty;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            var shell = provider.GetRequiredService<GtkNavigationService>().Shell ?? throw new InvalidOperationException("No shell.");
            switch (op)
            {
                case "force-available": presenter.DebugSetForceAvailable(value == "on"); break;
                case "open":
                    if (!shell.DebugBriefingShown) shell.ToggleBriefing();
                    break;
                case "close":
                    if (shell.DebugBriefingShown) shell.ToggleBriefing();
                    break;
                case "date": Panel(presenter).Pager.SelectedIndex = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
                case "primary": Panel(presenter).Cards[int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)].PrimaryButton.Activate(); break;
                case "ignore": Panel(presenter).Cards[int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)].IgnoreButton.Activate(); break;
                case "state":
                    DebugActions.WriteText(value, $"available={presenter.IsAvailable} unseen={presenter.HasUnseenItems} shown={shell.DebugBriefingShown} overlay={shell.DebugBriefingOverlay}\n"
                        + (presenter.Panel?.Describe() ?? "no panel"));
                    break;
                default: throw new ArgumentException($"Unknown debug-briefing operation '{op}'.");
            }
        });
        Serilog.Log.Information("Debug action: briefing {Operation} {Value} done.", op, value);
    }

    private static Views.Extras.DailyBriefingPanel Panel(DailyBriefingPresenter presenter)
        => presenter.Panel ?? throw new InvalidOperationException("The briefing panel was not created.");

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-briefing failed."); }
    }
}
#endif
