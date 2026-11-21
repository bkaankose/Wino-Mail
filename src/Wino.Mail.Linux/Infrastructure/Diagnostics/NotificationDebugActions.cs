#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-settings-open-account-badge</c> <c>s</c> (an account address, or empty for the first account) opens that
/// account's unread badge page in Settings.
/// </summary>
internal static class NotificationDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-settings-open-account-badge", GLib.VariantType.New("s"));
        action.OnActivate += (sender, args) => Observe(OpenAsync(services, args.Parameter?.GetString(out _) ?? string.Empty));
        application.AddAction(action);
    }

    private static async Task OpenAsync(Func<IServiceProvider?> services, string address)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var accounts = await provider.GetRequiredService<IAccountService>().GetAccountsAsync();
        var account = accounts.FirstOrDefault(candidate => string.IsNullOrEmpty(address) || string.Equals(candidate.Address, address, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No account '{address}'.");
        await provider.GetRequiredService<ISettingsWindowPresenter>().ShowAsync(WinoPage.AccountUnreadBadgePage, account.Id);
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-settings-open-account-badge failed."); }
    }
}
#endif
