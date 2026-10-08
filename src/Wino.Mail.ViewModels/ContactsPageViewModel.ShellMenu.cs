using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The contacts pane. Every entry is a navigation item owned by this view model, so the
/// shell only has to host the collection.
/// </summary>
public partial class ContactsPageViewModel
{
    private readonly NewContactMenuItem _newContactMenuItem = new();
    private readonly NewAddressListMenuItem _newAddressListMenuItem = new();
    private readonly SeperatorItem _accountSeparator = new();
    private readonly ShellSectionHeaderMenuItem _addressBooksHeader = new(Translator.ContactsPage_AddressBooks);
    private readonly ShellSectionHeaderMenuItem _listsHeader = new(Translator.ContactDetail_Lists);
    private readonly ContactCategoriesExpanderMenuItem _categoriesExpander = new(Translator.MailCategoryManagementPage_Title);

    private ShellMenu _shellMenu;
    private Guid? _activeAccountId;
    private bool _isMenuInteractionEnabled = true;
    private bool _isPreparedForShellShutdown;

    public IShellMenuProvider ShellMenuProvider => this;

    public WinoApplicationMode Mode => WinoApplicationMode.Contacts;

    public override async Task KeyboardShortcutHook(KeyboardShortcutTriggerDetails args)
    {
        if (args.Handled || args.Mode != WinoApplicationMode.Contacts ||
            args.InputContext is not KeyboardShortcutInputContext.Contacts)
        {
            return;
        }

        if (args.Action == KeyboardShortcutAction.NewContact)
        {
            // Without a writable address book the editor has nowhere to save to.
            if (AddContactCommand.CanExecute(null))
            {
                await AddContactAsync();
                args.Handled = true;
            }

            return;
        }

        if (args.Action != KeyboardShortcutAction.Delete)
            return;

        if (IsSelectionMode || SelectedContacts.Count > 1)
        {
            if (SelectedContacts.Any(contact => contact.IsEditable))
            {
                await DeleteSelectedContactsAsync();
                args.Handled = true;
            }

            return;
        }

        if (SelectedContact?.IsEditable == true)
        {
            await DeleteContactAsync(SelectedContact);
            args.Handled = true;
        }
    }

    public ShellMenu ShellMenu => _shellMenu;

    object IShellMenuProvider.SelectedMenuItem
    {
        get => SelectedFilter;
        set
        {
            if (value is ContactFilterViewModel filter)
            {
                SelectedFilter = filter;
            }
        }
    }

    protected override void OnDispatcherAssigned()
    {
        base.OnDispatcherAssigned();

        _isPreparedForShellShutdown = false;
        _shellMenu = new ShellMenu
        {
            Items = new MenuItemCollection(Dispatcher),
            HandlesSelection = true
        };

        // This view model outlives its page. Returning from the editor creates a new page,
        // which assigns the dispatcher again, while the filters built earlier stay as they
        // are. Without them the pane would be published empty with a filter still selected.
        SyncShellMenuItems();
    }

    public void ActivateShellMenu(ShellModeActivationContext activationContext)
        => _navigationService.Navigate(WinoPage.ContactsPage, activationContext?.Parameter);

    /// <summary>
    /// Mode switch. The pane items stay cached so returning to contacts does not rebuild
    /// the whole sidebar; the shell releases its item containers by dropping the menu.
    /// </summary>
    public void ReleaseShellMenu()
    {
        foreach (var filter in FilterGroups.SelectMany(group => group))
        {
            filter.IsDraggingItemOver = false;
        }
    }

    /// <summary>
    /// Window teardown. Everything goes.
    /// </summary>
    public void PrepareForShellShutdown()
    {
        if (_isPreparedForShellShutdown)
            return;

        _isPreparedForShellShutdown = true;
        _isPageActive = false;
        Interlocked.Increment(ref _currentQueryVersion);
        CancelPendingReload();
        UnregisterRecipients();
        SelectedContacts.CollectionChanged -= SelectedContactsChanged;

        SelectedFilter = null;
        SelectedContact = null;
        _isMenuInteractionEnabled = true;
        _activeAccountId = null;
        _shellMenu?.Items.Clear();
        _shellMenu = null;

        Contacts.Clear();
        SelectedContacts.Clear();
        ContactGroups.Clear();
        ContactLists.Clear();
        FilterGroups.Clear();
        _primaryFilterGroup.Clear();
        _accountFilterGroup.Clear();
        _addressBookFilterGroup.Clear();
        _listFilterGroup.Clear();
        _categoryFilterGroup.Clear();
        _accounts.Clear();

        _isInitialized = false;
        _currentOffset = 0;
        SelectedContactsCount = 0;
        TotalContactsCount = 0;
        HasMoreContacts = false;
        ListScrollOffset = null;
    }

    public Task OnMenuItemInvokedAsync(IMenuItem menuItem)
    {
        if (!_isMenuInteractionEnabled)
            return Task.CompletedTask;

        switch (menuItem)
        {
            case NewContactMenuItem:
                return AddContactCommand.CanExecute(null) ? AddContactAsync() : Task.CompletedTask;
            case NewAddressListMenuItem:
                return CreateListCommand.CanExecute(null) ? CreateListCommand.ExecuteAsync(null) : Task.CompletedTask;
            case ContactCategoriesExpanderMenuItem expander:
                // The state is kept on the entry, so it holds while the account changes.
                expander.IsExpanded = !expander.IsExpanded;
                return SyncShellMenuItemsAfterSelectionAsync();
            case ContactFilterViewModel filter:
                SelectedFilter = filter;
                break;
        }

        return Task.CompletedTask;
    }

    public Task OnMenuSelectionChangedAsync(IMenuItem menuItem)
    {
        if (!_isMenuInteractionEnabled)
            return Task.CompletedTask;

        if (menuItem is ContactFilterViewModel filter)
        {
            SelectedFilter = filter;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Every entry draws an icon, so a collapsed pane keeps the whole list.
    /// </summary>
    public void SetPaneCompact(bool isCompact) { }

    /// <summary>
    /// Projects the filters onto the flat navigation item collection, laid out like the
    /// mail pane: the command entries, the primary filters and the accounts, then what
    /// the active account holds: its address books and its lists under a caption each,
    /// and its categories behind an expandable entry. An empty section is left out.
    /// </summary>
    private void SyncShellMenuItems()
    {
        if (_shellMenu is null)
            return;

        var desired = new List<IMenuItem>(FilterGroups.Sum(group => group.Count) + 5)
        {
            _newContactMenuItem,
            _newAddressListMenuItem
        };

        desired.AddRange(_primaryFilterGroup);

        if (_accountFilterGroup.Count > 0)
        {
            desired.Add(_accountSeparator);
            desired.AddRange(_accountFilterGroup);
        }

        if (GetActiveAccountId() is Guid activeAccountId)
        {
            AddSection(desired, _addressBooksHeader, _addressBookFilterGroup.Where(filter => filter.AccountId == activeAccountId));
            AddSection(desired, _listsHeader, _listFilterGroup.Where(filter => filter.List?.MailAccountId == activeAccountId));

            // Categories stay behind one entry until it is expanded.
            var categories = _categoryFilterGroup.Where(filter => filter.AccountId == activeAccountId).ToList();
            if (categories.Count > 0)
            {
                desired.Add(_categoriesExpander);

                if (_categoriesExpander.IsExpanded)
                    desired.AddRange(categories);
            }
        }

        ApplyDesiredMenuItems(desired);
        ApplyMenuInteractionState();
        ApplyFilterSelectionStates();

        // The selected entry may have just been hidden or brought back; nudge the shell so
        // the pane re-applies the selection it should be showing.
        OnPropertyChanged(nameof(IShellMenuProvider.SelectedMenuItem));
    }

    private static void AddSection(List<IMenuItem> desired, IMenuItem header, IEnumerable<IMenuItem> items)
    {
        var section = items.ToList();
        if (section.Count == 0)
            return;

        desired.Add(header);
        desired.AddRange(section);
    }

    /// <summary>
    /// A selection usually arrives from the pane's own selection event. Its items are left
    /// alone until that event has finished, as the mail pane does when it swaps folders.
    /// </summary>
    private async Task SyncShellMenuItemsAfterSelectionAsync()
    {
        await Task.Yield();
        await ExecuteUIThread(SyncShellMenuItems);
    }

    /// <summary>
    /// The account being browsed. Like the loaded account in mail it stays active while
    /// another filter, or one of its address books or lists, is selected.
    /// </summary>
    private Guid? GetActiveAccountId()
        => (_accountFilterGroup.FirstOrDefault(filter => filter.AccountId == _activeAccountId)
            ?? _accountFilterGroup.FirstOrDefault())?.AccountId;

    /// <summary>
    /// Account rows draw the shared account row, which shows its active state from the
    /// item rather than from the pane's own selection visual.
    /// </summary>
    private void ApplyFilterSelectionStates()
    {
        var activeAccountId = GetActiveAccountId();

        foreach (var filter in FilterGroups.SelectMany(group => group))
        {
            filter.IsSelected = filter.Kind == ContactFilterKind.Account
                ? filter.AccountId == activeAccountId
                : ReferenceEquals(filter, SelectedFilter);
        }
    }

    /// <summary>
    /// Same approach as the mail pane's folder area: the unchanged leading entries stay, the
    /// rest is removed one by one and the new tail is appended. The navigation view cannot
    /// take moves or inserts in the middle of its items without crashing.
    /// </summary>
    private void ApplyDesiredMenuItems(List<IMenuItem> desired)
    {
        var items = _shellMenu.Items;

        var keepCount = 0;
        while (keepCount < items.Count && keepCount < desired.Count && ReferenceEquals(items[keepCount], desired[keepCount]))
        {
            keepCount++;
        }

        if (keepCount == items.Count && keepCount == desired.Count)
            return;

        var desiredSet = new HashSet<IMenuItem>(desired);

        for (var index = items.Count - 1; index >= keepCount; index--)
        {
            var item = items[index];

            // Entries leaving the pane must not take a selection with them.
            if (!desiredSet.Contains(item))
            {
                item.IsExpanded = false;
                item.IsSelected = false;
            }

            items.RemoveAt(index);
        }

        if (keepCount < desired.Count)
        {
            items.AddRange(desired.Skip(keepCount).ToList());
        }
    }

    /// <summary>
    /// The editor is a detail page inside this mode, so the pane keeps showing the contacts
    /// menu while it is open. Invoking a filter there would only change a selection nobody
    /// can see, so every entry is disabled for as long as this page is not the one on screen.
    /// </summary>
    private void SetMenuInteractionEnabled(bool isEnabled)
    {
        if (_isMenuInteractionEnabled == isEnabled)
            return;

        _isMenuInteractionEnabled = isEnabled;
        ApplyMenuInteractionState();
    }

    /// <summary>Keeps the pane's sync entry in step with a refresh started from anywhere.</summary>
    partial void OnIsRefreshingChanged(bool value) => ApplyMenuInteractionState();

    private void ApplyMenuInteractionState()
    {
        // The pane templates hide these entries while disabled, so they only show while the
        // page is on screen and there is at least one address book to create into.
        _newContactMenuItem.IsEnabled = _isMenuInteractionEnabled && HasCreateDestinations;
        _newAddressListMenuItem.IsEnabled = _isMenuInteractionEnabled && HasListDestinations;
        _categoriesExpander.IsEnabled = _isMenuInteractionEnabled;

        // A refresh already in flight must not be re-entered from the pane.

        foreach (var filter in FilterGroups.SelectMany(group => group))
        {
            filter.IsEnabled = _isMenuInteractionEnabled;
        }
    }

    /// <summary>
    /// Gives every list entry the callbacks it needs to service its own context menu and
    /// drop target, so the pane templates stay free of code-behind.
    /// </summary>
    private void AttachFilterCallbacks(ContactFilterViewModel filter)
    {
        filter.DropHandler = (list, contactIds) => AssignContactsToListAsync(list, contactIds);
        filter.RenameRequested = item => RenameListCommand.Execute(item);
        filter.DeleteRequested = item => DeleteListCommand.Execute(item);
        filter.SynchronizeAccountRequested = SynchronizeAccountAsync;
    }
}
