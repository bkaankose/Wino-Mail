#if DEBUG
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.ViewModels.Data;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Debug bridge commands that open the account sub-pages with the parameter their ViewModels expect:
/// <c>acct-folders [n]</c> and <c>acct-badges [n]</c> take the n-th account (default 0) and pass its Id;
/// <c>acct-merged [new]</c> opens the first linked inbox, or an unsaved one (as Create linked accounts
/// does) when there is none or <c>new</c> is given. Navigation only; nothing is written.
/// </summary>
internal static class AccountExtrasDebug
{
    private static IServiceProvider Services => typeof(MacDebugBridge).GetField("_services", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IServiceProvider
        ?? throw new InvalidOperationException("no service provider");

    public static void Register()
    {
        MacDebugBridge.Register("acct-folders", args => NavigateToAccountAsync(WinoPage.FolderCustomizationPage, args));
        MacDebugBridge.Register("acct-badges", args => NavigateToAccountAsync(WinoPage.AccountUnreadBadgePage, args));
        MacDebugBridge.Register("acct-merged", async args =>
        {
            var accounts = await AccountsAsync();
            var catalog = Services.GetRequiredService<IKnownImapProviderCatalog>();
            AccountProviderDetailViewModel Detail(MailAccount account) => new(catalog.GetProviderDetail(account.ProviderType), account);

            var group = args.Any(arg => arg.Equals("new", StringComparison.OrdinalIgnoreCase))
                ? null
                : accounts.Where(account => account.MergedInboxId != null && account.MergedInbox != null).GroupBy(account => account.MergedInboxId).FirstOrDefault();
            var merged = group is null
                ? new MergedAccountProviderDetailViewModel(new MergedInbox { Id = Guid.Empty, Name = "Linked inbox" }, [])
                : new MergedAccountProviderDetailViewModel(group.First().MergedInbox, group.Select(Detail).ToList());
            return await NavigateAsync(WinoPage.MergedAccountDetailsPage, merged) + (group is null ? " (unsaved)" : $" ({merged.MergedInbox.Name})");
        });
    }

    private static async Task<string> NavigateToAccountAsync(WinoPage page, string[] args)
    {
        var accounts = await AccountsAsync();
        var index = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 0;
        if (index < 0 || index >= accounts.Count) return $"no account at {index} ({accounts.Count} accounts)";
        return await NavigateAsync(page, accounts[index].Id) + $" ({accounts[index].Name})";
    }

    private static async Task<string> NavigateAsync(WinoPage page, object parameter)
    {
        var navigation = Services.GetRequiredService<AppKitNavigationService>();
        var navigated = false;
        await Services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() => navigated = navigation.Navigate(page, parameter));
        return navigated ? "ok" : "refused";
    }

    private static async Task<List<MailAccount>> AccountsAsync() => await Services.GetRequiredService<IAccountService>().GetAccountsAsync() ?? [];
}
#endif
