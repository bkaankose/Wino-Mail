using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Calendar.ViewModels;

/// <summary>
/// The calendar pane. The date picker and the per-account calendar groups are navigation
/// items like any other, which is what lets the shell stay unaware of calendar mode.
/// </summary>
public partial class CalendarAppShellViewModel
{
    private const string CalendarsSectionTitle = "Calendars";

    private readonly NewCalendarEventMenuItem _newEventMenuItem = new();
    private readonly ShellSectionHeaderMenuItem _calendarsSectionHeader = new(CalendarsSectionTitle);

    // These two carry the mode view model so their templates can bind straight to it.
    private CalendarSyncMenuItem _syncMenuItem;
    private CalendarDatePickerMenuItem _datePickerMenuItem;
    private readonly Dictionary<GroupedAccountCalendarViewModel, AccountCalendarGroupMenuItem> _accountCalendarMenuItems = [];

    private bool _isAccountCalendarSubscriptionAttached;
    private bool _isPaneCompact;

    public ShellMenu ShellMenu { get; private set; }

    public void ActivateShellMenu(ShellModeActivationContext activationContext)
    {
        AttachAccountCalendarSubscription();
        OnNavigatedTo(NavigationMode.New, activationContext);
    }

    /// <summary>
    /// Mode switch. Runtime subscriptions go, but the pane items stay cached so returning
    /// to the calendar does not rebuild the account calendar tree.
    /// </summary>
    public void ReleaseShellMenu() => OnNavigatedFrom(NavigationMode.New, null);

    public Task OnMenuItemInvokedAsync(IMenuItem menuItem)
        => menuItem == null ? Task.CompletedTask : HandleNavigationItemInvokedAsync(menuItem);

    public Task OnMenuSelectionChangedAsync(IMenuItem menuItem) => Task.CompletedTask;

    private void BuildShellMenu()
    {
        _syncMenuItem ??= new CalendarSyncMenuItem(this);
        _datePickerMenuItem ??= new CalendarDatePickerMenuItem(this);

        ShellMenu ??= new ShellMenu
        {
            Items = MenuItems,
            FooterItems = FooterItems,
            HandlesSelection = false
        };

        SyncShellMenuItems();
    }

    private void AttachAccountCalendarSubscription()
    {
        if (_isAccountCalendarSubscriptionAttached)
            return;

        if (AccountCalendarStateService.GroupedAccountCalendars is INotifyCollectionChanged observableGroups)
        {
            observableGroups.CollectionChanged += GroupedAccountCalendarsChanged;
            _isAccountCalendarSubscriptionAttached = true;
        }
    }

    private void DetachAccountCalendarSubscription()
    {
        if (!_isAccountCalendarSubscriptionAttached)
            return;

        if (AccountCalendarStateService.GroupedAccountCalendars is INotifyCollectionChanged observableGroups)
        {
            observableGroups.CollectionChanged -= GroupedAccountCalendarsChanged;
        }

        _isAccountCalendarSubscriptionAttached = false;
    }

    private void GroupedAccountCalendarsChanged(object sender, NotifyCollectionChangedEventArgs e)
        => _ = ExecuteUIThread(SyncShellMenuItems);

    /// <summary>
    /// Projects the current account calendar groups onto the pane. Item instances are
    /// reused so the pane never flickers while accounts synchronize.
    /// </summary>
    /// <summary>
    /// A collapsed pane is an icon-only strip. The date picker, the section caption and the
    /// per-account calendar expanders all draw real content rather than a navigation item
    /// icon, so they are dropped instead of being squeezed into it.
    /// </summary>
    public void SetPaneCompact(bool isCompact)
    {
        if (_isPaneCompact == isCompact)
            return;

        _isPaneCompact = isCompact;

        _ = ExecuteUIThread(SyncShellMenuItems);
    }

    private void SyncShellMenuItems()
    {
        if (ShellMenu is null)
            return;

        var groups = AccountCalendarStateService.GroupedAccountCalendars;
        var desired = new List<IMenuItem>(groups.Count + 4)
        {
            _newEventMenuItem,
            _syncMenuItem
        };

        if (_isPaneCompact)
        {
            ApplyDesiredMenuItems(desired);
            PruneAccountCalendarMenuItems();
            return;
        }

        desired.Add(_datePickerMenuItem);

        if (groups.Count > 0)
        {
            desired.Add(_calendarsSectionHeader);

            foreach (var group in groups)
            {
                desired.Add(GetAccountCalendarMenuItem(group));
            }
        }

        ApplyDesiredMenuItems(desired);
        PruneAccountCalendarMenuItems();
    }

    private AccountCalendarGroupMenuItem GetAccountCalendarMenuItem(GroupedAccountCalendarViewModel group)
    {
        if (!_accountCalendarMenuItems.TryGetValue(group, out var menuItem))
        {
            menuItem = new AccountCalendarGroupMenuItem(group);
            _accountCalendarMenuItems.Add(group, menuItem);
        }

        return menuItem;
    }

    private void PruneAccountCalendarMenuItems()
    {
        foreach (var group in _accountCalendarMenuItems.Keys.ToList())
        {
            if (!AccountCalendarStateService.GroupedAccountCalendars.Contains(group))
            {
                _accountCalendarMenuItems.Remove(group);
            }
        }
    }

    private void ApplyDesiredMenuItems(List<IMenuItem> desired)
    {
        var items = ShellMenu.Items;

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
}
