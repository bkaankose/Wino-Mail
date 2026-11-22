#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Linux.Views.Account;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-wino</c> (<c>as</c>): <c>['login'|'register'|'export']</c> opens the dialog through the dialog service and
/// logs the kind of result; <c>['confirm', seconds]</c> opens the confirmation dialog with fake details; while one is
/// open <c>['set', email|password|name|confirm, value]</c>, <c>['forgot']</c>, <c>['submit']</c>, <c>['external', i]</c>,
/// <c>['close']</c>; <c>['popover']</c> runs <c>app.account</c>, <c>['popover-click', signin|register|manage|signout]</c>, <c>['popover-shot', png]</c>;
/// <c>['button-hidden', 'true'|'false']</c>; <c>['state', path]</c>. Typed values are never logged.
/// </summary>
internal static class WinoAccountDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-wino", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) =>
        {
            var parts = args.Parameter?.GetStrv(out _) ?? [];
            Observe(RunAsync(application, services, parts));
        };
        application.AddAction(action);
    }

    private static async Task RunAsync(Adw.Application application, Func<IServiceProvider?> services, string[] parts)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var dialogs = provider.GetRequiredService<IMailDialogService>();
        var op = parts.Length > 0 ? parts[0] : string.Empty;
        var value = parts.Length > 1 ? parts[1] : string.Empty;
        switch (op)
        {
            case "login":
            {
                var account = await dialogs.ShowWinoAccountLoginDialogAsync();
                Serilog.Log.Information("Debug action: wino login dialog ended, account {Account}.", account is null ? "null" : "present");
                break;
            }
            case "register":
            {
                var account = await dialogs.ShowWinoAccountRegistrationDialogAsync();
                Serilog.Log.Information("Debug action: wino registration dialog ended, account {Account}.", account is null ? "null" : "present");
                break;
            }
            case "export":
                Serilog.Log.Information("Debug action: wino export dialog answered {Answer}.", await dialogs.ShowWinoAccountExportDialogAsync());
                break;
            case "confirm":
            {
                var seconds = int.TryParse(value, out var parsed) ? parsed : 5;
                var details = new EmailConfirmationRequiredDetailsDto("debug-endpoint", "debug-ticket", null, DateTimeOffset.UtcNow.AddSeconds(seconds));
                var profile = provider.GetRequiredService<IWinoAccountProfileService>();
                var owner = provider.GetRequiredService<GtkDialogService>();
                var resent = await RunOnUIAsync(provider, () => owner.ResolveOwner() is { } window
                    ? new WinoAccountEmailConfirmationDialog(profile, "someone@example.invalid", details).PresentAsync(window)
                    : Task.FromResult(false));
                Serilog.Log.Information("Debug action: wino confirmation dialog ended, resent {Resent}.", resent);
                break;
            }
            case "set" when parts.Length >= 3:
                await OnUI(provider, () =>
                {
                    if (WinoAccountLoginDialog.Current is { } login)
                    {
                        if (value == "email") login.DebugSetEmail(parts[2]);
                        else if (value == "password") login.DebugSetPassword(parts[2]);
                    }
                    else WinoAccountRegistrationDialog.Current?.DebugSet(value, parts[2]);
                });
                Serilog.Log.Information("Debug action: wino set {Field}.", value);
                break;
            case "forgot":
                await OnUI(provider, () => WinoAccountLoginDialog.Current?.DebugToggleMode());
                break;
            case "submit":
                await OnUI(provider, () =>
                {
                    if (WinoAccountLoginDialog.Current is { } login) login.DebugConfirm();
                    else WinoAccountRegistrationDialog.Current?.DebugConfirm();
                });
                break;
            case "external" when int.TryParse(value, out var index):
                await OnUI(provider, () => WinoAccountLoginDialog.Current?.DebugExternal(index));
                break;
            case "close":
                await OnUI(provider, () =>
                {
                    WinoAccountLoginDialog.Current?.DebugClose();
                    WinoAccountRegistrationDialog.Current?.DebugClose();
                    WinoAccountEmailConfirmationDialog.Current?.DebugClose();
                });
                break;
            case "popover":
                await OnUI(provider, () => application.ActivateAction(AppActions.Account, null));
                break;
            case "popover-click":
                await OnUI(provider, () => provider.GetRequiredService<WinoAccountPresenter>().Popover?.DebugClick(value));
                break;
            case "popover-shot":
                await OnUI(provider, () =>
                {
                    if (provider.GetRequiredService<WinoAccountPresenter>().Popover is { } popover) PopoverDebug.Capture(popover.Root, value);
                });
                break;
            case "button-hidden":
                provider.GetRequiredService<IPreferencesService>().IsWinoAccountButtonHidden = value == "true";
                Serilog.Log.Information("Debug action: wino button hidden {Hidden}.", value);
                break;
            case "state":
            {
                var text = new StringBuilder();
                var account = await provider.GetRequiredService<WinoAccountPresenter>().GetActiveAccountAsync();
                await OnUI(provider, () =>
                {
                    text.AppendLine($"signedIn={account is not null} buttonHidden={provider.GetRequiredService<IPreferencesService>().IsWinoAccountButtonHidden}");
                    text.AppendLine($"login: {WinoAccountLoginDialog.Current?.Describe() ?? "-"}");
                    text.AppendLine($"register: {WinoAccountRegistrationDialog.Current?.Describe() ?? "-"}");
                    text.AppendLine($"confirm: {WinoAccountEmailConfirmationDialog.Current?.Describe() ?? "-"}");
                    text.AppendLine($"popover: {provider.GetRequiredService<WinoAccountPresenter>().Popover?.Describe() ?? "-"}");
                });
                await File.WriteAllTextAsync(value, text.ToString());
                Serilog.Log.Information("Debug action: wino state written to {Path}.", value);
                break;
            }
            default:
                throw new ArgumentException($"Unknown debug-wino operation '{op}'.");
        }
    }

    private static Task OnUI(IServiceProvider provider, Action action) => provider.GetRequiredService<IDispatcher>().ExecuteOnUIThread(action);

    private static async Task<T> RunOnUIAsync<T>(IServiceProvider provider, Func<Task<T>> run)
    {
        Task<T>? task = null;
        await provider.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() => task = run());
        return task is null ? throw new InvalidOperationException("The dialog did not start.") : await task;
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-wino failed."); }
    }
}
#endif
