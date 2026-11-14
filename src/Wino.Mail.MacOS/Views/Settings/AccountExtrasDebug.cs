#if DEBUG
using System.Reflection;
using AppKit;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Debug bridge commands for the account sub-pages. <c>acct-folders [n]</c> and <c>acct-badges [n]</c> open the
/// n-th account (default 0); <c>acct-group [new|n]</c> opens the n-th account group (default 0), or a new one.
/// <c>group-page ARGS</c> drives the open account group page through its ViewModel, as its controls do:
/// <c>state</c>, <c>name TEXT</c>, <c>color KEY|default</c>, <c>icon NAME</c>, <c>combine on|off</c>, <c>add N</c>,
/// <c>remove N</c>, <c>move FROM TO</c>, <c>save</c>, <c>cancel</c>, <c>delete</c>; <c>confirm</c> presses the
/// default button of the open alert sheet.
/// </summary>
internal static class AccountExtrasDebug
{
    public static WeakReference<AccountGroupDetailsPageViewModel>? CurrentGroupPage { get; set; }

    private static IServiceProvider Services => typeof(MacDebugBridge).GetField("_services", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IServiceProvider
        ?? throw new InvalidOperationException("no service provider");

    public static void Register()
    {
        MacDebugBridge.Register("acct-folders", args => NavigateToAccountAsync(WinoPage.FolderCustomizationPage, args));
        MacDebugBridge.Register("acct-badges", args => NavigateToAccountAsync(WinoPage.AccountUnreadBadgePage, args));
        MacDebugBridge.Register("acct-group", async args =>
        {
            if (args.Any(arg => arg.Equals("new", StringComparison.OrdinalIgnoreCase)))
                return await NavigateAsync(WinoPage.MergedAccountDetailsPage, new AccountGroupDetailsNavigationContext(null)) + " (new)";
            var groups = await Services.GetRequiredService<IAccountService>().GetAccountGroupsAsync();
            var index = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 0;
            if (index < 0 || index >= groups.Count) return $"no group at {index} ({groups.Count} groups)";
            return await NavigateAsync(WinoPage.MergedAccountDetailsPage, new AccountGroupDetailsNavigationContext(groups[index].Id)) + $" ({groups[index].Name})";
        });
        MacDebugBridge.Register("group-page", async args =>
        {
            string result = "no page";
            await Services.GetRequiredService<IDispatcher>().ExecuteOnUIThread(() => result = GroupPage(args));
            return result;
        });
    }

    private static string GroupPage(string[] args)
    {
        if (args.Length > 0 && args[0] == "confirm") return ConfirmAlert();
        if (CurrentGroupPage is null || !CurrentGroupPage.TryGetTarget(out var vm)) return "no page";
        var value = string.Join(' ', args.Skip(1));
        switch (args.FirstOrDefault())
        {
            case "name":
                vm.GroupName = value;
                break;
            case "color":
                vm.SelectedColorOption = vm.ColorOptions.FirstOrDefault(option => value == "default" ? option.IsDefault : string.Equals(option.Key, value, StringComparison.OrdinalIgnoreCase)) ?? vm.SelectedColorOption;
                break;
            case "icon":
                vm.SelectedIconOption = vm.IconOptions.FirstOrDefault(option => string.Equals(option.Icon.ToString(), value, StringComparison.OrdinalIgnoreCase)) ?? vm.SelectedIconOption;
                break;
            case "combine":
                vm.CombineFolders = value == "on";
                break;
            case "add" when int.TryParse(value, out var add) && add >= 0 && add < vm.AvailableEntries.Count:
                vm.AddMemberCommand.Execute(vm.AvailableEntries[add]);
                break;
            case "remove" when int.TryParse(value, out var remove) && remove >= 0 && remove < vm.MemberEntries.Count:
                vm.RemoveMemberCommand.Execute(vm.MemberEntries[remove]);
                break;
            case "move" when args.Length > 2 && int.TryParse(args[1], out var from) && int.TryParse(args[2], out var to):
                vm.MemberEntries.Move(from, to);
                break;
            case "save":
                if (!vm.SaveCommand.CanExecute(null)) return "save disabled";
                vm.SaveCommand.Execute(null);
                return "saving";
            case "cancel":
                vm.CancelCommand.Execute(null);
                return "cancelled";
            case "delete":
                if (!vm.DeleteGroupCommand.CanExecute(null)) return "delete disabled";
                vm.DeleteGroupCommand.Execute(null);
                return "deleting";
        }
        return $"new={vm.IsNewGroup} name='{vm.GroupName}' color={vm.SelectedColorKey ?? "default"} icon={vm.SelectedIconOption?.Icon} combine={vm.CombineFolders} " +
            $"canSave={vm.SaveCommand.CanExecute(null)} memberMissing={vm.IsMemberMissing} members=[{string.Join(", ", vm.MemberEntries.Select(e => e.Title))}] " +
            $"available=[{string.Join(", ", vm.AvailableEntries.Select(e => e.IsInOtherGroup ? $"{e.Title} ({e.OtherGroupText})" : e.Title))}]";
    }

    private static string ConfirmAlert()
    {
        foreach (var window in NSApplication.SharedApplication.DangerousWindows)
        {
            if (!window.IsVisible || window.SheetParent is null || window.ContentView is not { } content) continue;
            if (FindDefaultButton(content) is { } button)
            {
                var title = button.Title;
                button.PerformClick(button);
                return "pressed " + title;
            }
        }
        return "no alert";
    }

    private static NSButton? FindDefaultButton(NSView view)
    {
        if (view is NSButton { KeyEquivalent: "\r" } button) return button;
        foreach (var child in view.Subviews)
            if (FindDefaultButton(child) is { } found) return found;
        return null;
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
