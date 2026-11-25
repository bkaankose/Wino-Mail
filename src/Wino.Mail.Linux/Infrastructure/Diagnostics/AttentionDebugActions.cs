#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels;
using Wino.Services.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-attention-state 'path'</c> writes per account its attention reason and whether signing in fixes it, then the
/// banner and the idle page; <c>debug-attention-break 'address'</c> replaces the stored mail passwords of that account
/// with a random value through the call the server settings form saves with, so the server's rejection sets the attention
/// state; <c>debug-attention-restore 'address'</c> puts the remembered passwords back the same way (the form's Save,
/// which also clears the attention). No action sets an attention reason by hand. Passwords are never logged.
/// </summary>
internal static class AttentionDebugActions
{
    private static readonly Dictionary<Guid, (string? Incoming, string? Outgoing)> Saved = [];

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, "debug-attention-state", value => Observe(StateAsync(services, value)));
        Add(application, "debug-attention-break", value => Observe(SetPasswordsAsync(services, value, restore: false)));
        Add(application, "debug-attention-restore", value => Observe(SetPasswordsAsync(services, value, restore: true)));
        Add(application, "debug-attention-idle", value => Observe(IdleAsync(services, value)));
    }

    private static void Add(Adw.Application application, string name, Action<string> run)
    {
        var action = Gio.SimpleAction.New(name, GLib.VariantType.New("s"));
        action.OnActivate += (sender, args) => run(args.Parameter?.GetString(out _) ?? string.Empty);
        application.AddAction(action);
    }

    private static async Task StateAsync(Func<IServiceProvider?> services, string path)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var lines = new List<string>();
        foreach (var account in await provider.GetRequiredService<IAccountService>().GetAccountsAsync())
            lines.Add($"account {account.Address} reason={account.AttentionReason} signInFixes={account.CanBeFixedBySigningIn()}");
        var navigation = provider.GetRequiredService<GtkNavigationService>();
        lines.Add("banner " + (navigation.Shell?.Attention.Describe() ?? "none"));
        lines.Add(navigation.RenderingFrameViewModel is IdlePageViewModel idle
            ? $"idle accountState={idle.IsAccountStateVisible} title={idle.AccountStateTitle} signIn={idle.IsSignInVisible} empty={idle.IsMailEmptyStateVisible}"
            : "idle not shown");
        DebugActions.WriteText(path, string.Join('\n', lines) + "\n");
    }

    /// <summary><c>debug-attention-idle 'signin'|'loading'|'empty'|''</c> shows the idle page in that state, for the first account.</summary>
    private static async Task IdleAsync(Func<IServiceProvider?> services, string state)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var account = (await provider.GetRequiredService<IAccountService>().GetAccountsAsync()).FirstOrDefault();
        object? parameter = state switch
        {
            "empty" => IdlePageViewModel.MailEmptyStateParameter,
            "signin" or "loading" when account is not null => new MailAccountIdleState(account.Id, account.Address, state == "signin"),
            _ => null,
        };
        await provider.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() =>
            provider.GetRequiredService<INavigationService>().Navigate(WinoPage.IdlePage, parameter, NavigationReferenceFrame.RenderingFrame));
        Serilog.Log.Information("Debug action: idle page shown in state {State}.", state.Length == 0 ? "idle" : state);
    }

    private static async Task SetPasswordsAsync(Func<IServiceProvider?> services, string address, bool restore)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var accounts = provider.GetRequiredService<IAccountService>();
        var match = (await accounts.GetAccountsAsync()).FirstOrDefault(account => string.Equals(account.Address, address, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No account {address}.");
        var account = await accounts.GetAccountAsync(match.Id);
        if (account.ServerInformation is not { } server) throw new InvalidOperationException($"{address} has no server settings.");

        if (restore)
        {
            if (!Saved.Remove(account.Id, out var saved)) throw new InvalidOperationException($"Nothing was broken for {address}.");
            server.IncomingServerPassword = saved.Incoming;
            server.OutgoingServerPassword = saved.Outgoing;
            account.AttentionReason = AccountAttentionReason.None;
        }
        else
        {
            Saved.TryAdd(account.Id, (server.IncomingServerPassword, server.OutgoingServerPassword));
            var wrong = Guid.NewGuid().ToString("N");
            server.IncomingServerPassword = wrong;
            server.OutgoingServerPassword = wrong;
        }
        account.ServerInformation = server;
        await accounts.UpdateImapConnectionSettingsAsync(account, server);
        await provider.GetRequiredService<ISynchronizationManager>().DestroySynchronizerAsync(account.Id);
        Serilog.Log.Information("Debug action: attention passwords {Action} for one account.", restore ? "restored" : "replaced");
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "An attention debug action failed."); }
    }
}
#endif
