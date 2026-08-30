using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly Dictionary<ContactFilterGroup, SeperatorItem> _groupSeparators = [];

    private ShellMenu _shellMenu;
    private bool _isMenuInteractionEnabled = true;
    private bool _isPreparedForShellShutdown;

    public IShellMenuProvider ShellMenuProvider => this;

    public WinoApplicationMode Mode => WinoApplicationMode.Contacts;

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
        _shellMenu?.Items.Clear();
        _shellMenu = null;
        _groupSeparators.Clear();

        Contacts.Clear();
        SelectedContacts.Clear();
        ContactGroups.Clear();
        ContactLists.Clear();
        FilterGroups.Clear();
        _primaryFilterGroup.Clear();
        _addressBookFilterGroup.Clear();
        _listFilterGroup.Clear();
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
                return AddContactAsync();
            case NewAddressListMenuItem:
                return CreateListCommand.ExecuteAsync(null);
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
    /// Projects the reconciled filter groups onto the flat navigation item collection.
    /// Existing item instances are reused and only moved, so the pane never loses its
    /// selection while address books or lists come and go.
    /// </summary>
    private void SyncShellMenuItems()
    {
        if (_shellMenu is null)
            return;

        var desired = new List<IMenuItem>(FilterGroups.Sum(group => group.Count) + FilterGroups.Count + 3)
        {
            _newContactMenuItem,
            _newAddressListMenuItem
        };

        var isFirstGroup = true;

        foreach (var group in FilterGroups)
        {
            // The primary filters sit directly under the command entries; every group
            // after them is fenced off with a rule instead of a caption.
            if (!isFirstGroup)
            {
                desired.Add(GetGroupSeparator(group));
            }

            desired.AddRange(group);
            isFirstGroup = false;
        }

        ApplyDesiredMenuItems(desired);
        PruneGroupSeparators();
        ApplyMenuInteractionState();

        // The selected entry may have just been hidden or brought back; nudge the shell so
        // the pane re-applies the selection it should be showing.
        OnPropertyChanged(nameof(IShellMenuProvider.SelectedMenuItem));
    }

    private SeperatorItem GetGroupSeparator(ContactFilterGroup group)
    {
        if (!_groupSeparators.TryGetValue(group, out var separator))
        {
            separator = new SeperatorItem();
            _groupSeparators.Add(group, separator);
        }

        return separator;
    }

    private void PruneGroupSeparators()
    {
        foreach (var group in _groupSeparators.Keys.ToList())
        {
            if (!FilterGroups.Contains(group))
            {
                _groupSeparators.Remove(group);
            }
        }
    }

    private void ApplyDesiredMenuItems(List<IMenuItem> desired)
    {
        var items = _shellMenu.Items;

        for (var index = 0; index < desired.Count; index++)
        {
            if (index >= items.Count)
            {
                items.Add(desired[index]);
                continue;
            }

            if (ReferenceEquals(items[index], desired[index]))
                continue;

            var currentIndex = items.IndexOf(desired[index]);

            if (currentIndex > index)
                items.Move(currentIndex, index);
            else
                items.Insert(index, desired[index]);
        }

        while (items.Count > desired.Count)
        {
            items.RemoveAt(items.Count - 1);
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
        _newContactMenuItem.IsEnabled = _isMenuInteractionEnabled;
        _newAddressListMenuItem.IsEnabled = _isMenuInteractionEnabled;

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
    }
}
