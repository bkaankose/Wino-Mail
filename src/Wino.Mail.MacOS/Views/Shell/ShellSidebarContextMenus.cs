using AppKit;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Shell;

/// <summary>
/// What a right click in the shell pane hit: the row's menu item and, for rows that draw several
/// models in one cell (an account's calendar group), the model under the pointer.
/// </summary>
internal sealed record ShellSidebarMenuTarget(IMenuItem Item, object? Part = null)
{
    /// <summary>The calendar a calendar row stands for, from a grouped calendar row or an ungrouped one.</summary>
    public AccountCalendarViewModel? Calendar => Part as AccountCalendarViewModel ?? (Item as UngroupedCalendarMenuItem)?.Parameter;
}

/// <summary>Services the pane's context menus act through.</summary>
internal sealed record ShellSidebarMenuContext(
    IMailShellClient? Mail,
    AppKitNavigationService Navigation,
    IDispatcher Dispatcher,
    Action<Exception> Error,
    Func<IAccountNavigationMenuItem, Task> FixAccount);

/// <summary>
/// Native context menus for the shell pane, built from the same shared services and menu item
/// callbacks as the Windows ShellMenuTemplates context flyouts: folder operations
/// (<see cref="IMailShellClient.GetFolderContextMenuActions"/>), account actions, merged inbox,
/// contact lists and categories, To Do groups and lists. Other features append their own entries
/// through <see cref="AddExtension"/>.
/// </summary>
internal static class ShellSidebarContextMenus
{
    private const double IconSize = 16;
    private static readonly List<Func<ShellSidebarMenuTarget, ShellSidebarMenuContext, IEnumerable<NSMenuItem>>> Extensions = new();

    /// <summary>
    /// Adds entries for rows other features own (for example "Calendar settings" on calendar rows).
    /// The provider returns nothing for rows it does not handle; its entries follow the built-in ones
    /// after a separator.
    /// </summary>
    public static void AddExtension(Func<ShellSidebarMenuTarget, ShellSidebarMenuContext, IEnumerable<NSMenuItem>> provider)
    {
        lock (Extensions) Extensions.Add(provider);
    }

    /// <summary>A new menu for the target, or null when the row has no actions.</summary>
    public static NSMenu? Build(ShellSidebarMenuTarget target, ShellSidebarMenuContext context)
    {
        var menu = new NSMenu { AutoEnablesItems = false };
        Populate(menu, target, context);
        return menu.Count > 0 ? menu : null;
    }

    /// <summary>Replaces the menu's items with the target's actions.</summary>
    public static void Populate(NSMenu menu, ShellSidebarMenuTarget target, ShellSidebarMenuContext context)
    {
        menu.RemoveAllItems();
        menu.AutoEnablesItems = false;
        var builtIn = BuiltInItems(target, context);
        foreach (var item in builtIn) menu.AddItem(item);

        Func<ShellSidebarMenuTarget, ShellSidebarMenuContext, IEnumerable<NSMenuItem>>[] providers;
        lock (Extensions) providers = Extensions.ToArray();
        bool separated = menu.Count == 0;
        foreach (var provider in providers)
        {
            IEnumerable<NSMenuItem> extra;
            try { extra = provider(target, context)?.ToList() ?? []; }
            catch (Exception exception) { context.Error(exception); continue; }
            foreach (var item in extra)
            {
                if (!separated) { menu.AddItem(NSMenuItem.SeparatorItem); separated = true; }
                menu.AddItem(item);
            }
        }
        TrimSeparators(menu);
    }

    private static IReadOnlyList<NSMenuItem> BuiltInItems(ShellSidebarMenuTarget target, ShellSidebarMenuContext context) => target.Item switch
    {
        FolderMenuItem or MergedAccountFolderMenuItem when target.Item is IBaseFolderMenuItem { IsMoveTarget: true } folder => FolderItems(folder, context),
        ContactFilterViewModel { HasAccountIcon: false } list => ContactListItems(list, context),
        MergedAccountMenuItem merged => MergedAccountItems(merged, context),
        IAccountNavigationMenuItem account when ShellPaneRows.IsAccountRow(account) => AccountItems(account, context),
        AccountTaskListGroupMenuItem group => TaskGroupItems(group, context),
        AccountTaskListMenuItem list => TaskListItems(list, context),
        _ => []
    };

    // ---- Folders (Windows MenuItemContextRequested + FolderOperationFlyout)

    private static IReadOnlyList<NSMenuItem> FolderItems(IBaseFolderMenuItem folder, ShellSidebarMenuContext context)
    {
        if (context.Mail is not { } mail) return [];
        var items = new List<NSMenuItem>();
        foreach (var action in mail.GetFolderContextMenuActions(folder))
        {
            if (action.Operation == FolderOperation.Seperator)
            {
                items.Add(NSMenuItem.SeparatorItem);
                continue;
            }
            var title = FolderOperationPresentation.MenuTitle(action.Operation);
            if (string.IsNullOrEmpty(title)) continue;
            var operation = action.Operation;
            var item = Item(title, WinoIconGlyph.None, () => mail.PerformFolderOperationAsync(operation, folder), context, action.IsEnabled);
            item.Image = FolderOperationPresentation.Image(operation, IconSize);
            items.Add(item);
        }
        return items;
    }

    // ---- Accounts (Windows AccountContextRequested / MergedAccountContextRequested)

    private static IReadOnlyList<NSMenuItem> AccountItems(IAccountNavigationMenuItem account, ShellSidebarMenuContext context)
    {
        var items = new List<NSMenuItem>
        {
            Item(Translator.AccountContextMenu_ManageAccountSettings, WinoIconGlyph.ManageAccounts, () => OpenAccountSettings(account, context), context)
        };

        // A pending sign-in can only make Sync fail, so the menu offers the fix in its place.
        if (account.IsAttentionRequired)
            items.Add(Item(Translator.Buttons_FixAccount, WinoIconGlyph.Warning, () => FixAccountAsync(account, context), context));
        else if (account.SupportsAccountSynchronization)
            items.Add(Item(Translator.Buttons_Sync, WinoIconGlyph.Sync, account.SynchronizeAccountAsync, context));

        if (account is AccountMenuItem mailAccount && account.SupportsMailAccountActions && context.Mail is { } mail)
        {
            items.Add(Item($"{Translator.AccountContextMenu_CreateFolder}…", WinoIconGlyph.CreateFolder, () => mail.CreateRootFolderAsync(mailAccount), context));
            items.Add(MarkInboxesAsRead(mailAccount, context));
        }
        return items;
    }

    private static IReadOnlyList<NSMenuItem> MergedAccountItems(MergedAccountMenuItem merged, ShellSidebarMenuContext context)
    {
        if (context.Mail is null) return [];
        return [MarkInboxesAsRead(merged, context)];
    }

    private static NSMenuItem MarkInboxesAsRead(IAccountMenuItem account, ShellSidebarMenuContext context)
        => Item(Translator.FolderOperation_MarkAllAsRead, WinoIconGlyph.MarkRead, () => context.Mail!.MarkAccountInboxesAsReadAsync(account), context);

    private static Task OpenAccountSettings(IAccountNavigationMenuItem account, ShellSidebarMenuContext context)
    {
        if (account.Account is { } mailAccount)
            context.Navigation.Navigate(WinoPage.AccountDetailsPage, new AccountDetailsNavigationContext(mailAccount.Id, account.AccountDetailsTab));
        return Task.CompletedTask;
    }

    /// <summary>Every mode runs the mail shell's fix, like the Windows pane; the shell's own handler is the fallback.</summary>
    private static Task FixAccountAsync(IAccountNavigationMenuItem account, ShellSidebarMenuContext context)
        => context.Mail is { } mail && account.Account is { } mailAccount
            ? mail.HandleAccountAttentionAsync(mailAccount)
            : context.FixAccount(account);

    // ---- Contacts (Windows ContactListContextRequested)

    private static IReadOnlyList<NSMenuItem> ContactListItems(ContactFilterViewModel list, ShellSidebarMenuContext context)
    {
        if (!list.CanRenameOrDelete) return [];
        var rename = Item($"{(list.IsCategory ? Translator.Buttons_Edit : Translator.ContactList_Rename)}…", WinoIconGlyph.Rename,
            () => { list.RenameListCommand.Execute(null); return Task.CompletedTask; }, context, list.RenameListCommand.CanExecute(null));
        var delete = Item(Translator.ContactsPage_Delete, WinoIconGlyph.Delete,
            () => { list.DeleteListCommand.Execute(null); return Task.CompletedTask; }, context, list.DeleteListCommand.CanExecute(null));
        return [rename, delete];
    }

    // ---- To Do (Windows TaskGroupContextRequested / TaskListContextRequested)

    private static IReadOnlyList<NSMenuItem> TaskGroupItems(AccountTaskListGroupMenuItem group, ShellSidebarMenuContext context)
    {
        var items = new List<NSMenuItem>();
        if (group.NewListRequested is { } newList)
            items.Add(Item($"{Translator.ToDoPage_NewList}…", WinoIconGlyph.New, () => newList(group), context));
        if (group.IsEditable && group.RenameRequested is { } rename)
            items.Add(Item($"{Translator.ToDoPage_RenameGroup}…", WinoIconGlyph.Rename, () => rename(group), context));
        if (group.CanUngroup && group.UngroupRequested is { } ungroup)
            items.Add(Item(Translator.ToDoPage_UngroupLists, WinoIconGlyph.Move, () => ungroup(group), context));
        if (group.CanDelete && group.DeleteRequested is { } delete)
            items.Add(Item(Translator.ToDoPage_DeleteGroup, WinoIconGlyph.Delete, () => delete(group), context));
        return items;
    }

    private static IReadOnlyList<NSMenuItem> TaskListItems(AccountTaskListMenuItem list, ShellSidebarMenuContext context)
    {
        var items = new List<NSMenuItem>();
        if (list.RenameRequested is { } rename)
            items.Add(Item($"{Translator.ToDoPage_RenameList}…", WinoIconGlyph.Rename, () => rename(list), context));
        if (list.IsGrouped && list.RemoveFromGroupRequested is { } remove)
            items.Add(Item(Translator.ToDoPage_RemoveFromGroup, WinoIconGlyph.Move, () => remove(list), context));
        if (list.CanMoveToGroup && list.MoveToGroupRequested is { } move)
        {
            var destinations = list.AvailableGroups.Where(group => group.Id != list.Parameter.GroupId).ToList();
            if (destinations.Count > 0)
            {
                var submenu = new NSMenu(Translator.ToDoPage_MoveToGroup) { AutoEnablesItems = false };
                foreach (var destination in destinations)
                {
                    var id = destination.Id;
                    submenu.AddItem(Item(destination.Title ?? string.Empty, WinoIconGlyph.Folder, () => move(list, id), context));
                }
                var parent = Item(Translator.ToDoPage_MoveToGroup, WinoIconGlyph.Move, null, context);
                parent.Submenu = submenu;
                items.Add(parent);
            }
        }
        if (list.CanDelete && list.DeleteRequested is { } delete)
        {
            items.Add(NSMenuItem.SeparatorItem);
            items.Add(Item(Translator.ToDoPage_DeleteList, WinoIconGlyph.Delete, () => delete(list), context));
        }
        return items;
    }

    // ---- Helpers

    /// <summary>A menu entry that runs <paramref name="action"/> and reports its failure instead of throwing.</summary>
    public static NSMenuItem Item(string title, WinoIconGlyph glyph, Func<Task>? action, ShellSidebarMenuContext context, bool enabled = true)
    {
        var item = new NSMenuItem(title) { Enabled = enabled };
        if (glyph != WinoIconGlyph.None) item.Image = WinoIcons.Image(glyph, IconSize, null, title);
        if (action is not null) item.Activated += (_, _) => Run(action, context);
        return item;
    }

    private static async void Run(Func<Task> action, ShellSidebarMenuContext context)
    {
        try { await action(); }
        catch (Exception exception) { context.Error(exception); }
    }

    /// <summary>Drops leading, trailing and doubled separators left by the shared action lists.</summary>
    private static void TrimSeparators(NSMenu menu)
    {
        for (nint index = menu.Count - 1; index >= 0; index--)
        {
            var item = menu.ItemAt(index);
            if (item is null || !item.IsSeparatorItem) continue;
            bool first = index == 0, last = index == menu.Count - 1;
            bool doubled = !first && menu.ItemAt(index - 1)?.IsSeparatorItem == true;
            if (first || last || doubled) menu.RemoveItemAt(index);
        }
    }

    /// <summary>The visible entries of a menu, for the debug bridge.</summary>
    public static IReadOnlyList<NSMenuItem> Entries(NSMenu menu)
    {
        var entries = new List<NSMenuItem>();
        for (nint index = 0; index < menu.Count; index++)
            if (menu.ItemAt(index) is { IsSeparatorItem: false } item) entries.Add(item);
        return entries;
    }
}
