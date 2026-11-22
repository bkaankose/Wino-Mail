#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Settings;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-purchase</c> (<c>as</c>): <c>['state', path]</c> writes the purchase capability, the Wino Account sign-in, the
/// pending checkout's product and, while Settings shows Manage accounts, its upsell state; <c>['channel']</c> shows the
/// purchase channel dialog and logs the answer.
/// </summary>
internal static class PurchaseDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-purchase", GLib.VariantType.New("as"));
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
        var op = parts.Length > 0 ? parts[0] : string.Empty;
        switch (op)
        {
            case "state" when parts.Length > 1:
                await File.WriteAllTextAsync(parts[1], await DescribeAsync(provider) + Environment.NewLine);
                Serilog.Log.Information("Debug action: purchase state written to {Path}.", parts[1]);
                break;
            case "channel":
            {
                var channel = await provider.GetRequiredService<IMailDialogService>().ShowUnlimitedAccountsPurchaseChannelDialogAsync();
                Serilog.Log.Information("Debug action: purchase channel result={Channel}.", channel?.ToString() ?? "null");
                break;
            }
            default:
                throw new ArgumentException($"Unknown debug-purchase operation '{op}'.");
        }
    }

    private static async Task<string> DescribeAsync(IServiceProvider provider)
    {
        var capabilities = provider.GetRequiredService<IPlatformCapabilities>();
        var profile = provider.GetRequiredService<IWinoAccountProfileService>();
        var account = await profile.GetActiveAccountAsync();
        var pending = account is null ? null : provider.GetRequiredService<IWinoPendingCheckoutStore>().Get(account.Id);
        var line = $"purchases={capabilities.Purchases} microsoftStore={capabilities.MicrosoftStore} appleAppStore={capabilities.AppleAppStore} " +
                   $"signedIn={account is { SessionInvalidatedUtc: null }} pending={pending?.ToString() ?? "none"}";
        var dispatcher = provider.GetRequiredService<IDispatcher>();
        var page = string.Empty;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            if (provider.GetRequiredService<SettingsWindowPresenter>().CurrentView is ManageAccountsPage { ViewModel: AccountManagementViewModel vm })
                page = $"\nmanageAccounts panelVisible={vm.IsPurchasePanelVisible} creationBlocked={vm.IsAccountCreationBlocked} used={vm.UsedAccountCount} " +
                       $"channels='{vm.PurchaseChannelsDescription}'";
        });
        return line + page;
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-purchase failed."); }
    }
}
#endif
