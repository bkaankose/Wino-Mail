#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-accounts</c> (<c>as</c>): <c>['state', path]</c> (accounts and groups read fresh from the service),
/// <c>['group-page', path]</c> (the open account group page), <c>['group-pick', 'color'|'icon', index]</c> (selects a
/// tile as a click does), <c>['lab-add', leasePath, server]</c> (adds a second leased lab account as <c>--lab-lease</c>
/// adds the first), <c>['details', address, tab]</c> (account details through Manage accounts, as Fix account opens
/// them), <c>['open', WinoPage, address]</c> (a page that takes the account id), <c>['pick', path]</c> (the next file
/// picker returns this path).
/// </summary>
internal static class AccountsDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-accounts", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => Observe(RunAsync(services, args.Parameter?.GetStrv(out _) ?? []));
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] parts)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        switch (parts)
        {
            case ["state", var path]:
            {
                var accountService = provider.GetRequiredService<IAccountService>();
                var text = new StringBuilder();
                foreach (var account in (await accountService.GetAccountsAsync()).OrderBy(account => account.Order))
                {
                    text.AppendLine($"account|{account.Name}|{account.Address}|sender={account.SenderName}|color={account.AccountColorHex}|group={account.MergedInbox?.Name}"
                                    + $"|attention={account.AttentionReason}|picture={account.ProfilePictureFileId.HasValue}|protocolLog={account.IsProtocolLogEnabled}"
                                    + $"|mail={account.IsMailAccessGranted}|calendar={account.IsCalendarAccessGranted}|contacts={account.IsContactAccessEnabled}|tasks={account.IsTaskAccessEnabled}");
                }
                foreach (var group in await accountService.GetAccountGroupsAsync())
                    text.AppendLine($"group|{group.Name}|color={group.ColorKey}|icon={group.Icon}|combine={group.CombineFolders}");
                DebugActions.WriteText(path, text.ToString());
                Serilog.Log.Information("Debug action: account state written to {Path}.", path);
                break;
            }
            case ["group-page", var path]:
                DebugActions.WriteText(path, AccountGroupDetailsPage.DebugCurrent?.DebugDescribe() ?? "no page\n");
                break;
            case ["group-pick", var grid, var index]:
                Serilog.Log.Information("Debug action: group {Grid} pick {Result}.", grid,
                    AccountGroupDetailsPage.DebugCurrent?.DebugPick(grid, int.Parse(index, System.Globalization.CultureInfo.InvariantCulture)) ?? false);
                break;
            case ["details", var address, var tab]:
            {
                var account = await AccountAsync(provider, address);
                if (!Enum.TryParse<AccountDetailsTab>(tab, out var selected)) selected = AccountDetailsTab.General;
                await provider.GetRequiredService<ISettingsWindowPresenter>().ShowAsync(WinoPage.ManageAccountsPage, new AccountDetailsNavigationContext(account.Id, selected));
                break;
            }
            case ["open", var page, var address]:
                await provider.GetRequiredService<ISettingsWindowPresenter>().ShowAsync(Enum.Parse<WinoPage>(page), (await AccountAsync(provider, address)).Id);
                break;
            case ["pick", var path]:
                FilePickers.DebugNext.Enqueue(path);
                Serilog.Log.Information("Debug action: the next file picker returns the injected path.");
                break;
            case ["lab-add", var leasePath, var server]:
                await LabAccountBootstrap.AddAsync(provider, provider.GetRequiredService<IDispatcher>(), leasePath, server);
                break;
            default:
                throw new ArgumentException($"Unknown debug-accounts operation '{string.Join(' ', parts)}'.");
        }
    }

    private static async Task<MailAccount> AccountAsync(IServiceProvider provider, string address)
        => (await provider.GetRequiredService<IAccountService>().GetAccountsAsync())
            .FirstOrDefault(account => string.Equals(account.Address, address, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException($"No account '{address}'.");

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-accounts failed."); }
    }
}
#endif
