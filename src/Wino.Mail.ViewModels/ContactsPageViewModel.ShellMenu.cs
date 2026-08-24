using System.Collections.Generic;
using System.Linq;
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
    private readonly Dictionary<ContactFilterGroup, ShellSectionHeaderMenuItem> _sectionHeaders = [];

    private ShellMenu _shellMenu;
    private bool _isPaneCompact;
    private bool _isMenuInteractionEnabled = true;

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

        _shellMenu ??= new ShellMenu
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
        SelectedFilter = null;
        _isMenuInteractionEnabled = true;
        _shellMenu?.Items.Clear();
        _sectionHeaders.Clear();
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
    /// A collapsed pane is an icon-only strip. Section captions have no icon at all, and
    /// address books draw an account picture in their content rather than in the navigation
    /// item's icon slot, so both are dropped rather than clipped.
    /// </summary>
    public void SetPaneCompact(bool isCompact)
    {
        if (_isPaneCompact == isCompact)
            return;

        _isPaneCompact = isCompact;

        _ = ExecuteUIThread(SyncShellMenuItems);
    }

    /// <summary>
    /// Projects the reconciled filter groups onto the flat navigation item collection.
    /// Existing item instances are reused and only moved, so the pane never loses its
    /// selection while address books or lists come and go.
    /// </summary>
    private void SyncShellMenuItems()
    {
        if (_shellMenu is null)
            return;

        var desired = new List<IMenuItem>(FilterGroups.Sum(group => group.Count) + FilterGroups.Count + 2)
        {
            _newContactMenuItem,
            _newAddressListMenuItem
        };

        foreach (var group in FilterGroups)
        {
            if (group.HasTitle && !_isPaneCompact)
            {
                desired.Add(GetSectionHeader(group));
            }

            desired.AddRange(group.Where(CanRenderInCurrentPane));
        }

        ApplyDesiredMenuItems(desired);
        PruneSectionHeaders();
        ApplyMenuInteractionState();

        // The selected entry may have just been hidden or brought back; nudge the shell so
        // the pane re-applies the selection it should be showing.
        OnPropertyChanged(nameof(IShellMenuProvider.SelectedMenuItem));
    }

    private bool CanRenderInCurrentPane(ContactFilterViewModel filter)
        => !_isPaneCompact || filter.HasGlyphIcon;

    private ShellSectionHeaderMenuItem GetSectionHeader(ContactFilterGroup group)
    {
        if (!_sectionHeaders.TryGetValue(group, out var header))
        {
            header = new ShellSectionHeaderMenuItem(group.Title);
            _sectionHeaders.Add(group, header);
        }

        return header;
    }

    private void PruneSectionHeaders()
    {
        foreach (var group in _sectionHeaders.Keys.ToList())
        {
            if (!FilterGroups.Contains(group))
            {
                _sectionHeaders.Remove(group);
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

    private void ApplyMenuInteractionState()
    {
        _newContactMenuItem.IsEnabled = _isMenuInteractionEnabled;
        _newAddressListMenuItem.IsEnabled = _isMenuInteractionEnabled;

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
