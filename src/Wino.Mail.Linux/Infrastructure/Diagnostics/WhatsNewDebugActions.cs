#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-whatsnew</c> (<c>as</c>): <c>['open']</c> opens the window as the header button does; <c>['state', path]</c>
/// writes the page state, the window list and whether the shell entry should show; <c>['select', version]</c> picks a
/// release as its pill does; <c>['next']</c> selects the next feature; <c>['reset-seen']</c> forgets the seen version.
/// </summary>
internal static class WhatsNewDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-whatsnew", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => Observe(RunAsync(services, args.Parameter?.GetStrv(out _) ?? []));
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] parts)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var presenter = provider.GetRequiredService<WhatsNewPresenter>();
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        switch (parts)
        {
            case ["open"]:
                await presenter.ShowAsync();
                break;
            case ["state", var path]:
            {
                var show = await provider.GetRequiredService<IWhatsNewService>().ShouldShowShellEntryAsync();
                await dispatcher.ExecuteOnUIThread(() =>
                {
                    var windows = provider.GetRequiredService<WindowManager>().Describe();
                    DebugActions.WriteText(path, $"shouldShowEntry={show}\nwindows={string.Join(';', windows)}\n"
                        + (presenter.DebugView?.DebugDescribe() ?? "no view\n"));
                });
                break;
            }
            case ["select", var version]:
                await dispatcher.ExecuteOnUIThread(() => View(presenter).DebugSelect(version));
                break;
            case ["next"]:
                await dispatcher.ExecuteOnUIThread(() => View(presenter).DebugNext());
                break;
            case ["reset-seen"]:
                provider.GetRequiredService<IConfigurationService>().Set("WhatsNew_LastOpenedVersion", string.Empty);
                break;
            default:
                throw new ArgumentException($"Unknown debug-whatsnew operation '{string.Join(' ', parts)}'.");
        }
        Serilog.Log.Information("Debug action: whatsnew {Operation} done.", parts.Length > 0 ? parts[0] : string.Empty);
    }

    private static Views.Extras.WhatsNewView View(WhatsNewPresenter presenter)
        => presenter.DebugView ?? throw new InvalidOperationException("The What's New window is not open.");

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-whatsnew failed."); }
    }
}
#endif
