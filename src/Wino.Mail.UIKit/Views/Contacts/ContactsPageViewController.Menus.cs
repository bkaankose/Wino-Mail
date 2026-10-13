using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Contacts;

public abstract partial class ContactsPageViewController
{
    // ---- Swipe actions (leading favourite; trailing delete) ----

    private UISwipeActionsConfiguration? SwipeActions(NSIndexPath indexPath, bool leading)
    {
        if (ContactAt(indexPath) is not { } contact) return null;
        UIContextualAction[] actions;
        if (leading)
        {
            actions =
            [
                ModeBarItems.Swipe(contact.FavoriteActionText, contact.IsFavorite ? WinoIconGlyph.Star : WinoIconGlyph.StarFilled,
                    WinoStyle.Caution, () => ToggleFavorite(contact))
            ];
        }
        else
        {
            if (!contact.CanDelete) return null;
            actions =
            [
                ModeBarItems.Swipe(Translator.Buttons_Delete, WinoIconGlyph.Delete, WinoStyle.Critical,
                    () => Observe(ViewModel.DeleteContactsAsync([contact])), UIContextualActionStyle.Destructive)
            ];
        }
        var configuration = UISwipeActionsConfiguration.FromActions(actions);
        configuration.PerformsFirstActionWithFullSwipe = leading;
        return configuration;
    }

    // ---- Long-press menu (Windows ContactCardMenuFlyout, Mac PopulateContextMenu) ----

    private UIContextMenuConfiguration? ContextMenu(NSIndexPath indexPath)
    {
        if (ContactAt(indexPath) is not { } contact) return null;
        return UIContextMenuConfiguration.Create(null, null, _ => BuildContextMenu(contact));
    }

    /// <summary>
    /// The Mac context menu over the contacts it targets (the selection when the row is part of it, else the row): Send
    /// mail, Edit, Favourite, Add to list (deferred: membership is a query), Category toggles, Remove from this list and
    /// Delete. Send mail and Delete stay and disable when no target qualifies.
    /// </summary>
    private UIMenu BuildContextMenu(AccountContactViewModel contact)
    {
        var targets = ViewModel.ResolveContactContextTargets(contact);
        var primary = new List<UIMenuElement>
        {
            ModeBarItems.Action(Translator.ContactAction_SendMail, WinoIconGlyph.Mail, () => ViewModel.ComposeToContacts(targets), targets.Any(item => item.CanSendMail))
        };
        if (targets.Count == 1)
            primary.Add(ModeBarItems.Action(Translator.ContactAction_Edit, WinoIconGlyph.Edit, () => ViewModel.EditContactCommand.Execute(contact), contact.CanEdit));
        var anyNotFavorite = targets.Any(item => !item.IsFavorite);
        primary.Add(ModeBarItems.Action(anyNotFavorite ? Translator.ContactAction_Favorite : Translator.ContactAction_Unfavorite,
            anyNotFavorite ? WinoIconGlyph.Star : WinoIconGlyph.StarFilled, () => Observe(ViewModel.FavoriteContactsAsync(targets))));

        var organize = new List<UIMenuElement>();
        if (AssignListMenu(targets) is { } assign) organize.Add(assign);
        if (CategoryMenu(targets) is { } categories) organize.Add(categories);
        if (ViewModel.SelectedFilter?.IsList == true)
            organize.Add(ModeBarItems.Action(Translator.ContactAction_RemoveFromList, WinoIconGlyph.List, () => Observe(RemoveFromCurrentListAsync(targets))));

        var destructive = ModeBarItems.Action(Translator.ContactAction_Delete, WinoIconGlyph.Delete,
            () => Observe(ViewModel.DeleteContactsAsync(targets)), targets.Any(item => item.CanDelete), destructive: true);

        var sections = new List<UIMenuElement> { ModeBarItems.Inline(primary.ToArray()) };
        if (organize.Count > 0) sections.Add(ModeBarItems.Inline(organize.ToArray()));
        sections.Add(ModeBarItems.Inline(destructive));
        return UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, 0, sections.ToArray());
    }

    /// <summary>"Add to list" with the lists of the targets' address book, loaded when the submenu opens.</summary>
    private UIMenu? AssignListMenu(IReadOnlyList<AccountContactViewModel> targets)
    {
        if (targets.Count == 0 || !ViewModel.ContactLists.Any(list => targets.All(item => item.SourceContact.AddressBookId == list.AddressBookId)))
            return null;
        var ids = targets.Select(contact => contact.Id).ToList();
        var deferred = UIDeferredMenuElement.CreateUncached(completion => Observe(FillAssignListAsync(targets, ids, completion)));
        return ModeBarItems.Submenu(Translator.ContactAction_AddToList, WinoIconGlyph.People, deferred);
    }

    private async Task FillAssignListAsync(IReadOnlyList<AccountContactViewModel> targets, List<Guid> ids, UIDeferredMenuElementCompletionHandler completion)
    {
        IReadOnlyList<ContactList> lists = [];
        try { lists = await ViewModel.GetAssignableListsAsync(targets); }
        finally
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                UIMenuElement[] elements = lists.Count == 0
                    ? [ModeBarItems.Action(Translator.ContactsShellMac_NoAssignableLists, WinoIconGlyph.None, () => { }, enabled: false)]
                    : lists.Select(list => (UIMenuElement)ModeBarItems.Action(list.Name ?? string.Empty, WinoIconGlyph.List,
                        () => Observe(ViewModel.AssignContactsToListAsync(list, ids)))).ToArray();
                completion(elements);
            });
        }
    }

    /// <summary>Category toggles: On when every target has the category, Mixed when some do.</summary>
    private UIMenu? CategoryMenu(IReadOnlyList<AccountContactViewModel> targets)
    {
        var categories = ViewModel.GetAvailableCategories(targets);
        if (categories.Count == 0) return null;
        var items = new List<UIMenuElement>();
        foreach (var category in categories)
        {
            var assigned = targets.Count(contact => contact.Categories.Any(item => item.Id == category.Id));
            var tint = WinoStyle.FromHexString(category.TextColorHex) ?? WinoStyle.FromHexString(category.BackgroundColorHex);
            var action = UIAction.Create(category.Name ?? string.Empty, WinoIcons.MonoImage(WinoIconGlyph.Tag, 16, tint ?? WinoStyle.SecondaryText), null,
                _ => Observe(ViewModel.ToggleContactCategoryAsync(category, targets)));
            action.State = assigned == 0 ? UIMenuElementState.Off : assigned == targets.Count ? UIMenuElementState.On : UIMenuElementState.Mixed;
            items.Add(action);
        }
        return ModeBarItems.Submenu(Translator.MailCategoryMenuItem, WinoIconGlyph.Tag, items.ToArray());
    }

    /// <summary>Removes every target from the list the page is filtered to (the ViewModel removes one contact per request).</summary>
    private async Task RemoveFromCurrentListAsync(IReadOnlyList<AccountContactViewModel> targets)
    {
        foreach (var contact in targets.ToList())
            await ViewModel.RemoveFromCurrentListCommand.ExecuteAsync(contact);
    }

    // ---- Filters (iPhone): the contacts pane as a menu, in the Windows pane order ----

    private UIBarButtonItem FilterBarItem()
    {
        var menu = UIMenu.Create([UIDeferredMenuElement.CreateUncached(completion => completion(FilterMenuElements()))]);
        return ModeBarItems.MenuButton(WinoIconGlyph.Filter, Translator.ToDoPage_Filter, menu);
    }

    /// <summary>
    /// The pane's sections as inline menu groups: the primary filters and accounts, the active account's address books
    /// and lists under their captions, and its categories as a submenu. New contact and New list stay commands.
    /// </summary>
    private UIMenuElement[] FilterMenuElements()
    {
        var elements = new List<UIMenuElement>();
        var section = new List<UIMenuElement>();
        string? title = null;

        void Flush()
        {
            if (section.Count == 0) return;
            elements.Add(UIMenu.Create(title ?? string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, section.ToArray()));
            section = new List<UIMenuElement>();
        }

        foreach (var item in ViewModel.ShellMenu?.Items.ToArray() ?? [])
        {
            switch (item)
            {
                case NewContactMenuItem:
                    break;
                case NewAddressListMenuItem when ViewModel.CreateListCommand.CanExecute(null):
                    section.Add(ModeBarItems.Action(Translator.ContactList_NewTitle, WinoIconGlyph.PersonAdd, () => Observe(ViewModel.OnMenuItemInvokedAsync(item))));
                    break;
                case SeperatorItem:
                    Flush();
                    title = null;
                    break;
                case ShellSectionHeaderMenuItem header:
                    Flush();
                    title = header.Title;
                    break;
                case ContactCategoriesExpanderMenuItem:
                    var accountId = ViewModel.FilterGroups.SelectMany(group => group)
                        .FirstOrDefault(filter => filter.Kind == ContactFilterKind.Account && filter.IsSelected)?.AccountId;
                    var categories = ViewModel.FilterGroups.SelectMany(group => group)
                        .Where(filter => filter.IsCategory && (accountId is null || filter.AccountId == accountId))
                        .Select(filter => (UIMenuElement)FilterAction(filter, WinoIconGlyph.Tag)).ToArray();
                    if (categories.Length > 0)
                    {
                        Flush();
                        title = null;
                        elements.Add(ModeBarItems.Submenu(Translator.MailCategoryManagementPage_Title, WinoIconGlyph.Tag, categories));
                    }
                    break;
                case ContactFilterViewModel filter when !filter.IsCategory:
                    section.Add(FilterAction(filter, filter.Kind == ContactFilterKind.Account ? WinoIconGlyph.Person : filter.IsList ? WinoIconGlyph.List : WinoIconGlyph.People));
                    break;
            }
        }
        Flush();
        return elements.ToArray();
    }

    private UIAction FilterAction(ContactFilterViewModel filter, WinoIconGlyph glyph)
    {
        var title = filter.Count > 0 && filter.Kind != ContactFilterKind.Account ? $"{filter.Name}  {filter.Count}" : filter.Name ?? string.Empty;
        return ModeBarItems.Action(title, glyph, () => Observe(ViewModel.OnMenuItemInvokedAsync(filter)), filter.IsEnabled,
            on: ReferenceEquals(filter, ViewModel.SelectedFilter));
    }

#if DEBUG
    // ---- Debug bridge ----

    private void RegisterDebugCommands()
    {
        UIKitDebugBridge.Register("contacts-count", _ => Task.FromResult(
            $"{ViewModel.Contacts.Count} contacts, {_sections.Count} sections, loading={ViewModel.IsLoading}, blocked={ViewModel.Readiness.IsBlocked}, filter={ViewModel.SelectedFilter?.Name}"));
        UIKitDebugBridge.Register("contacts-select", args =>
        {
            int index = args.Length > 0 && int.TryParse(args[0], out var parsed) ? parsed : 0;
            var contact = _sections.SelectMany(section => section.Contacts).Skip(index).FirstOrDefault();
            if (contact is null) return Task.FromResult("no contact");
            ViewModel.SelectedContacts.Clear();
            ViewModel.SelectedContacts.Add(contact);
            ViewModel.SelectedContact = contact;
            ContactActivated(contact);
            return Task.FromResult("ok " + contact.Name);
        });
        UIKitDebugBridge.Register("contacts-edit", _ =>
        {
            if (ViewModel.SelectedContact is not { } contact) return Task.FromResult("no selection");
            ViewModel.EditContactCommand.Execute(contact);
            return Task.FromResult("ok");
        });
        UIKitDebugBridge.Register("contacts-new", _ => { AddContact(); return Task.FromResult("ok"); });
        UIKitDebugBridge.Register("contacts-search", async args => { await SearchTextChangedAsync(string.Join(' ', args)); return "ok " + (_searchResults?.Count ?? 0); });
        UIKitDebugBridge.Register("contacts-filters", _ => Task.FromResult(string.Join(" | ", FilterMenuElements().Select(element => element.Title))));
        UIKitDebugBridge.Register("contacts-menu", _ =>
        {
            if (ViewModel.SelectedContact is not { } contact) return Task.FromResult("no selection");
            var menu = BuildContextMenu(contact);
            return Task.FromResult(string.Join(" | ", menu.Children.OfType<UIMenu>().SelectMany(group => group.Children).Select(child => child.Title)));
        });
    }
#endif
}
