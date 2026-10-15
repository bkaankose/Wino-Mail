using Android.Content;
using Android.Views;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.ViewModels.Data;
using PopupMenu = AndroidX.AppCompat.Widget.PopupMenu;
using IMenuItem = Wino.Core.Domain.Interfaces.IMenuItem;

namespace Wino.Mail.Android.Views.Shell;

/// <summary>
/// Long-press menus on drawer rows (Windows ShellMenuTemplates context flyouts, iPhone Mailboxes menus): the shared
/// folder actions of the mail provider for folders, Mark all as read for linked accounts, and for accounts Manage
/// account settings, Fix account, Create folder and Mark all as read (no Synchronize: pull to refresh only).
/// </summary>
internal static class ShellMenuActions
{
    public static void Show(Context context, View anchor, IMenuItem item, IServiceProvider services)
    {
        var actions = Build(item, services);
        if (actions.Count == 0) return;
        var popup = new PopupMenu(context, anchor);
        int group = 0;
        for (int index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            if (action is null) { group++; continue; }
            var menuItem = popup.Menu.Add(group, index, index, action.Value.Title);
            menuItem?.SetEnabled(action.Value.Enabled);
        }
        popup.MenuItemClick += (_, args) =>
        {
            if (args.Item?.ItemId is int id && id >= 0 && id < actions.Count && actions[id] is { } chosen) _ = RunAsync(chosen.Run, services);
        };
        popup.Show();
    }

    private static List<(string Title, Func<Task> Run, bool Enabled)?> Build(IMenuItem item, IServiceProvider services)
    {
        var mail = services.GetService<IMailShellClient>();
        var actions = new List<(string Title, Func<Task> Run, bool Enabled)?>();
        switch (item)
        {
            case IBaseFolderMenuItem { IsMoveTarget: true } folder when item is FolderMenuItem or MergedAccountFolderMenuItem && mail is not null:
                foreach (var action in mail.GetFolderContextMenuActions(folder) ?? [])
                {
                    if (action.Operation == FolderOperation.Seperator) { actions.Add(null); continue; }
                    var title = FolderOperationDisplay.GetTitle(action.Operation);
                    if (string.IsNullOrEmpty(title)) continue;
                    var operation = action.Operation;
                    actions.Add((FolderOperationDisplay.AsksForInput(operation) ? $"{title}…" : title, () => mail.PerformFolderOperationAsync(operation, folder), action.IsEnabled));
                }
                break;
            case MergedAccountMenuItem merged when mail is not null:
                actions.Add((Translator.FolderOperation_MarkAllAsRead, () => mail.MarkAccountInboxesAsReadAsync(merged), true));
                break;
            case IAccountNavigationMenuItem account when ShellMenuItemRules.IsAccountRow(account):
                if (account.Account is { } details)
                    actions.Add((Translator.AccountContextMenu_ManageAccountSettings, () =>
                    {
                        services.GetRequiredService<INavigationService>().Navigate(WinoPage.AccountDetailsPage, new AccountDetailsNavigationContext(details.Id, account.AccountDetailsTab));
                        return Task.CompletedTask;
                    }, true));
                // No Sync action (canvas Material pass): synchronization is pull to refresh only.
                if (account.IsAttentionRequired && account.Account is { } attention && mail is not null)
                    actions.Add((Translator.Buttons_FixAccount, () => mail.HandleAccountAttentionAsync(attention), true));
                if (account is AccountMenuItem mailAccount && account.SupportsMailAccountActions && mail is not null)
                {
                    actions.Add((Translator.AccountContextMenu_CreateFolder + "…", () => mail.CreateRootFolderAsync(mailAccount), true));
                    actions.Add((Translator.FolderOperation_MarkAllAsRead, () => mail.MarkAccountInboxesAsReadAsync(mailAccount), true));
                }
                break;
        }
        // Separators only between groups.
        while (actions.Count > 0 && actions[^1] is null) actions.RemoveAt(actions.Count - 1);
        return actions;
    }

    private static async Task RunAsync(Func<Task> run, IServiceProvider services)
    {
        try { await run(); }
        catch (Exception exception) { services.GetService<IWinoLogger>()?.CaptureException(exception, nameof(ShellMenuActions)); }
    }
}
