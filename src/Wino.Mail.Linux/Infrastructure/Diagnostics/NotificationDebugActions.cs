#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// Account pages of Settings: <c>debug-settings-open-account-badge</c> and <c>debug-settings-open-calendar-account</c>
/// (<c>s</c>: an account address, or empty for the first account) open that account's unread badge page or its primary
/// calendar's settings.
/// </summary>
internal static class NotificationDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        Add(application, services, "debug-settings-open-account-badge", WinoPage.AccountUnreadBadgePage);
        Add(application, services, "debug-settings-open-calendar-account", WinoPage.CalendarAccountSettingsPage);
    }

    private static void Add(Adw.Application application, Func<IServiceProvider?> services, string name, WinoPage page)
    {
        var action = Gio.SimpleAction.New(name, GLib.VariantType.New("s"));
        action.OnActivate += (sender, args) => Observe(name, OpenAsync(services, args.Parameter?.GetString(out _) ?? string.Empty, page));
        application.AddAction(action);
    }

    private static async Task OpenAsync(Func<IServiceProvider?> services, string address, WinoPage page)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var accounts = await provider.GetRequiredService<IAccountService>().GetAccountsAsync();
        var account = accounts.FirstOrDefault(candidate => string.IsNullOrEmpty(address) || string.Equals(candidate.Address, address, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No account '{address}'.");
        await provider.GetRequiredService<ISettingsWindowPresenter>().ShowAsync(page, account.Id);
    }

    private static async void Observe(string name, Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Name} failed.", name); }
    }
}
#endif
