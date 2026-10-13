using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Presentation;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Pad.Shell;

/// <summary>Services the sidebar's context menus act through (the Mac ShellSidebarMenuContext).</summary>
internal sealed record PadSidebarMenuContext(
    Func<IMailShellClient?> Mail,
    Func<UIKitNavigationService?> Navigation,
    Action<Exception> Error,
    Func<IMenuItem, Task> FixAccount);

/// <summary>
/// Long-press menus for the sidebar rows (UIKit port of the Mac ShellSidebarContextMenus), built from the same shared
/// services and menu item callbacks as the Windows ShellMenuTemplates context flyouts: folder operations
/// (<see cref="IMailShellClient.GetFolderContextMenuActions"/>), account actions, the merged inbox, contact lists and
/// categories, To Do groups and lists.
/// </summary>
internal static class PadSidebarContextMenus
{
    private const double IconSize = 16;

    /// <summary>A new menu for the row, or null when it has no actions.</summary>
    public static UIMenu? Build(IMenuItem item, PadSidebarMenuContext context)
    {
        var groups = new List<UIMenuElement>();
        var current = new List<UIMenuElement>();
        foreach (var element in Items(item, context))
        {
            if (element is null)
            {
                if (current.Count > 0) groups.Add(Inline(current));
                current.Clear();
                continue;
            }
            current.Add(element);
        }
        if (current.Count > 0) groups.Add(Inline(current));
        return groups.Count == 0 ? null : UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, groups.ToArray());
    }

    private static UIMenu Inline(List<UIMenuElement> elements)
        => UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, elements.ToArray());

    /// <summary>The row's entries; null marks a separator.</summary>
    private static IEnumerable<UIMenuElement?> Items(IMenuItem item, PadSidebarMenuContext context) => item switch
    {
        FolderMenuItem or MergedAccountFolderMenuItem when item is IBaseFolderMenuItem { IsMoveTarget: true } folder => FolderItems(folder, context),
        ContactFilterViewModel { HasAccountIcon: false } list => ContactListItems(list, context),
        MergedAccountMenuItem merged => MergedAccountItems(merged, context),
        IAccountNavigationMenuItem account when ShellMenuItemRules.IsAccountRow(account) => AccountItems(account, context),
        AccountTaskListGroupMenuItem group => TaskGroupItems(group, context),
        AccountTaskListMenuItem list => TaskListItems(list, context),
        _ => Array.Empty<UIMenuElement?>()
    };

    // ---- Folders (Windows MenuItemContextRequested + FolderOperationFlyout)

    private static IEnumerable<UIMenuElement?> FolderItems(IBaseFolderMenuItem folder, PadSidebarMenuContext context)
    {
        if (context.Mail() is not { } mail) yield break;
        foreach (var action in mail.GetFolderContextMenuActions(folder))
        {
            if (action.Operation == FolderOperation.Seperator) { yield return null; continue; }
            var title = FolderOperationDisplay.GetTitle(action.Operation);
            if (string.IsNullOrEmpty(title)) continue;
            if (FolderOperationDisplay.AsksForInput(action.Operation)) title += "…";
            var operation = action.Operation;
            var element = Item(title, FolderOperationDisplay.GetGlyph(operation), () => mail.PerformFolderOperationAsync(operation, folder), context, action.IsEnabled);
            if (FolderOperationDisplay.IsDestructive(operation)) element.Attributes |= UIMenuElementAttributes.Destructive;
            yield return element;
        }
    }

    // ---- Accounts (Windows AccountContextRequested / MergedAccountContextRequested)

    private static IEnumerable<UIMenuElement?> AccountItems(IAccountNavigationMenuItem account, PadSidebarMenuContext context)
    {
        yield return Item(Translator.AccountContextMenu_ManageAccountSettings, WinoIconGlyph.ManageAccounts, () => OpenAccountSettings(account, context), context);

        // A pending sign-in can only make Sync fail, so the menu offers the fix in its place.
        if (account.IsAttentionRequired)
            yield return Item(Translator.Buttons_FixAccount, WinoIconGlyph.Warning, () => FixAccountAsync(account, context), context);
        else if (account.SupportsAccountSynchronization)
            yield return Item(Translator.Buttons_Sync, WinoIconGlyph.Sync, account.SynchronizeAccountAsync, context);

        if (account is AccountMenuItem mailAccount && account.SupportsMailAccountActions && context.Mail() is { } mail)
        {
            yield return Item($"{Translator.AccountContextMenu_CreateFolder}…", WinoIconGlyph.CreateFolder, () => mail.CreateRootFolderAsync(mailAccount), context);
            yield return MarkInboxesAsRead(mail, mailAccount, context);
        }
    }

    private static IEnumerable<UIMenuElement?> MergedAccountItems(MergedAccountMenuItem merged, PadSidebarMenuContext context)
    {
        if (context.Mail() is { } mail) yield return MarkInboxesAsRead(mail, merged, context);
    }

    private static UIAction MarkInboxesAsRead(IMailShellClient mail, IAccountMenuItem account, PadSidebarMenuContext context)
        => Item(Translator.FolderOperation_MarkAllAsRead, WinoIconGlyph.MarkRead, () => mail.MarkAccountInboxesAsReadAsync(account), context);

    private static Task OpenAccountSettings(IAccountNavigationMenuItem account, PadSidebarMenuContext context)
    {
        if (account.Account is { } mailAccount)
            context.Navigation()?.Navigate(WinoPage.AccountDetailsPage, new AccountDetailsNavigationContext(mailAccount.Id, account.AccountDetailsTab));
        return Task.CompletedTask;
    }

    /// <summary>Every mode runs the mail shell's fix, like the Windows pane; the shell's own handler is the fallback.</summary>
    private static Task FixAccountAsync(IAccountNavigationMenuItem account, PadSidebarMenuContext context)
        => context.Mail() is { } mail && account.Account is { } mailAccount
            ? mail.HandleAccountAttentionAsync(mailAccount)
            : context.FixAccount(account);

    // ---- Contacts (Windows ContactListContextRequested)

    private static IEnumerable<UIMenuElement?> ContactListItems(ContactFilterViewModel list, PadSidebarMenuContext context)
    {
        if (!list.CanRenameOrDelete) yield break;
        yield return Item($"{(list.IsCategory ? Translator.Buttons_Edit : Translator.ContactList_Rename)}…", WinoIconGlyph.Rename,
            () => { list.RenameListCommand.Execute(null); return Task.CompletedTask; }, context, list.RenameListCommand.CanExecute(null));
        var delete = Item(Translator.ContactsPage_Delete, WinoIconGlyph.Delete,
            () => { list.DeleteListCommand.Execute(null); return Task.CompletedTask; }, context, list.DeleteListCommand.CanExecute(null));
        delete.Attributes |= UIMenuElementAttributes.Destructive;
        yield return delete;
    }

    // ---- To Do (Windows TaskGroupContextRequested / TaskListContextRequested)

    private static IEnumerable<UIMenuElement?> TaskGroupItems(AccountTaskListGroupMenuItem group, PadSidebarMenuContext context)
    {
        if (group.NewListRequested is { } newList)
            yield return Item($"{Translator.ToDoPage_NewList}…", WinoIconGlyph.New, () => newList(group), context);
        if (group.IsEditable && group.RenameRequested is { } rename)
            yield return Item($"{Translator.ToDoPage_RenameGroup}…", WinoIconGlyph.Rename, () => rename(group), context);
        if (group.CanUngroup && group.UngroupRequested is { } ungroup)
            yield return Item(Translator.ToDoPage_UngroupLists, WinoIconGlyph.Move, () => ungroup(group), context);
        if (group.CanDelete && group.DeleteRequested is { } delete)
            yield return Item(Translator.ToDoPage_DeleteGroup, WinoIconGlyph.Delete, () => delete(group), context);
    }

    private static IEnumerable<UIMenuElement?> TaskListItems(AccountTaskListMenuItem list, PadSidebarMenuContext context)
    {
        if (list.RenameRequested is { } rename)
            yield return Item($"{Translator.ToDoPage_RenameList}…", WinoIconGlyph.Rename, () => rename(list), context);
        if (list.IsGrouped && list.RemoveFromGroupRequested is { } remove)
            yield return Item(Translator.ToDoPage_RemoveFromGroup, WinoIconGlyph.Move, () => remove(list), context);
        if (list.CanMoveToGroup && list.MoveToGroupRequested is { } move)
        {
            var destinations = list.AvailableGroups.Where(group => group.Id != list.Parameter.GroupId).ToList();
            if (destinations.Count > 0)
            {
                var children = destinations.Select(destination =>
                {
                    var id = destination.Id;
                    return (UIMenuElement)Item(destination.Title ?? string.Empty, WinoIconGlyph.Folder, () => move(list, id), context);
                }).ToArray();
                yield return UIMenu.Create(Translator.ToDoPage_MoveToGroup, WinoIcons.Image(WinoIconGlyph.Move, IconSize), UIMenuIdentifier.None, 0, children);
            }
        }
        if (list.CanDelete && list.DeleteRequested is { } delete)
        {
            yield return null;
            var element = Item(Translator.ToDoPage_DeleteList, WinoIconGlyph.Delete, () => delete(list), context);
            element.Attributes |= UIMenuElementAttributes.Destructive;
            yield return element;
        }
    }

    // ---- Helpers

    /// <summary>A menu entry that runs <paramref name="action"/> and reports its failure instead of throwing.</summary>
    private static UIAction Item(string title, WinoIconGlyph glyph, Func<Task> action, PadSidebarMenuContext context, bool enabled = true)
    {
        var image = glyph == WinoIconGlyph.None ? null : WinoIcons.Image(glyph, IconSize, null, title);
        var element = UIAction.Create(title, image, null, _ => Run(action, context));
        if (!enabled) element.Attributes |= UIMenuElementAttributes.Disabled;
        return element;
    }

    private static async void Run(Func<Task> action, PadSidebarMenuContext context)
    {
        try { await action(); }
        catch (Exception exception) { context.Error(exception); }
    }

    /// <summary>The entry titles of a menu, for the debug bridge.</summary>
    public static IEnumerable<string> Titles(UIMenu menu)
    {
        foreach (var child in menu.Children)
        {
            if (child is UIMenu submenu)
            {
                foreach (var title in Titles(submenu)) yield return string.IsNullOrEmpty(submenu.Title) ? title : $"{submenu.Title} › {title}";
                continue;
            }
            yield return child.Title;
        }
    }
}
