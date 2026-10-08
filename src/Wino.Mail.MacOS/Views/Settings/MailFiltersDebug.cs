#if DEBUG
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Debug bridge commands for the mail filter pages: <c>filters [n]</c> opens the n-th account's filter
/// list (default 0); <c>filter-edit [n] [m|new]</c> opens the editor for that account's m-th filter, or a
/// new filter when <c>new</c> is given or the account has none. Navigation only; nothing is written.
/// </summary>
internal static class MailFiltersDebug
{
    private static IServiceProvider Services => typeof(MacDebugBridge).GetField("_services", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IServiceProvider
        ?? throw new InvalidOperationException("no service provider");

    public static void Register()
    {
        MacDebugBridge.Register("filters", async args =>
        {
            var account = await AccountAsync(args);
            if (account is null) return "no such account";
            return await NavigateAsync(WinoPage.MailFiltersPage, account.Id) + $" ({account.Name})";
        });
        MacDebugBridge.Register("filter-edit", async args =>
        {
            var account = await AccountAsync(args);
            if (account is null) return "no such account";
            var which = args.Length > 1 ? args[1] : "0";
            if (which.Equals("new", StringComparison.OrdinalIgnoreCase))
                return await NavigateAsync(WinoPage.MailFilterEditorPage, new MailFilterEditorNavigationParameter(account.Id)) + " (new)";

            var filters = await Services.GetRequiredService<IMailFilterService>().GetFiltersAsync(account.Id) ?? [];
            var index = int.TryParse(which, out var parsed) ? parsed : 0;
            if (filters.Count == 0)
                return await NavigateAsync(WinoPage.MailFilterEditorPage, new MailFilterEditorNavigationParameter(account.Id)) + " (no filters, new)";
            if (index < 0 || index >= filters.Count) return $"no filter at {index} ({filters.Count} filters)";
            var filter = filters[index];
            return await NavigateAsync(WinoPage.MailFilterEditorPage, new MailFilterEditorNavigationParameter(account.Id, filter.Id)) + $" ({filter.Name})";
        });
    }

    private static async Task<MailAccount?> AccountAsync(string[] args)
    {
        var accounts = await Services.GetRequiredService<IAccountService>().GetAccountsAsync() ?? [];
        var index = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 0;
        return index >= 0 && index < accounts.Count ? accounts[index] : null;
    }

    private static async Task<string> NavigateAsync(WinoPage page, object parameter)
    {
        var navigation = Services.GetRequiredService<AppKitNavigationService>();
        var navigated = false;
        await Services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() => navigated = navigation.Navigate(page, parameter));
        return navigated ? "ok" : "refused";
    }
}
#endif
