using System;
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
/// The calendar pane. The date picker and account/calendar projections are navigation items
/// like any other, which is what lets the shell stay unaware of calendar mode.
/// </summary>
public partial class CalendarAppShellViewModel
{
    private const string CalendarsSectionTitle = "Calendars";

    private readonly NewCalendarEventMenuItem _newEventMenuItem = new();
    private readonly ShellSectionHeaderMenuItem _calendarsSectionHeader = new(CalendarsSectionTitle);
    private CalendarDatePickerMenuItem _datePickerMenuItem;
    private readonly Dictionary<GroupedAccountCalendarViewModel, AccountCalendarGroupMenuItem> _accountCalendarMenuItems = [];
    private readonly Dictionary<Guid, CalendarAccountMenuItem> _calendarAccountMenuItems = [];
    private readonly Dictionary<Guid, UngroupedCalendarMenuItem> _ungroupedCalendarMenuItems = [];
    private readonly HashSet<GroupedAccountCalendarViewModel> _subscribedAccountCalendarGroups = [];

    private bool _isAccountCalendarSubscriptionAttached;
    private bool _isAccountCalendarInitializationInProgress;
    private bool _hasCompletedAccountCalendarInitialization;
    private bool _isPaneCompact;
    private Guid? _selectedCalendarAccountId;

    public ShellMenu ShellMenu { get; private set; }

    public void ActivateShellMenu(ShellModeActivationContext activationContext)
    {
        AttachAccountCalendarSubscription();
        OnNavigatedTo(NavigationMode.New, activationContext);
    }

    public void ReleaseShellMenu() => OnNavigatedFrom(NavigationMode.New, null);

    public Task OnMenuItemInvokedAsync(IMenuItem menuItem)
        => menuItem == null ? Task.CompletedTask : HandleNavigationItemInvokedAsync(menuItem);

    public Task OnMenuSelectionChangedAsync(IMenuItem menuItem) => Task.CompletedTask;

    private void BuildShellMenu()
    {
        _datePickerMenuItem = new CalendarDatePickerMenuItem(this, PreferencesService);

        ShellMenu = new ShellMenu
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

        RefreshAccountCalendarGroupSubscriptions();
    }

    private void DetachAccountCalendarSubscription()
    {
        if (!_isAccountCalendarSubscriptionAttached)
            return;

        if (AccountCalendarStateService.GroupedAccountCalendars is INotifyCollectionChanged observableGroups)
        {
            observableGroups.CollectionChanged -= GroupedAccountCalendarsChanged;
        }

        foreach (var group in _subscribedAccountCalendarGroups)
        {
            group.AccountCalendars.CollectionChanged -= AccountCalendarsChanged;
        }

        _subscribedAccountCalendarGroups.Clear();
        _isAccountCalendarSubscriptionAttached = false;
    }

    private void GroupedAccountCalendarsChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isAccountCalendarInitializationInProgress)
            return;

        _ = ExecuteUIThread(() =>
        {
            RefreshAccountCalendarGroupSubscriptions();
            SyncShellMenuItems();
        });
    }

    private void AccountCalendarsChanged(object sender, NotifyCollectionChangedEventArgs e)
        => _ = ExecuteUIThread(SyncShellMenuItems);

    private void RefreshAccountCalendarGroupSubscriptions()
    {
        var currentGroups = AccountCalendarStateService.GroupedAccountCalendars.ToHashSet();

        foreach (var group in _subscribedAccountCalendarGroups.Where(group => !currentGroups.Contains(group)).ToList())
        {
            group.AccountCalendars.CollectionChanged -= AccountCalendarsChanged;
            _subscribedAccountCalendarGroups.Remove(group);
        }

        foreach (var group in currentGroups.Where(group => !_subscribedAccountCalendarGroups.Contains(group)))
        {
            group.AccountCalendars.CollectionChanged += AccountCalendarsChanged;
            _subscribedAccountCalendarGroups.Add(group);
        }
    }

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
            _newEventMenuItem
        };

        if (_isPaneCompact)
        {
            ApplyDesiredMenuItems(desired);
            PruneShellMenuItemCaches();
            return;
        }

        desired.Add(_datePickerMenuItem);

        if (groups.Count > 0)
        {
            desired.Add(_calendarsSectionHeader);

            if (PreferencesService.IsCalendarAccountsGrouped)
            {
                foreach (var group in groups)
                {
                    desired.Add(GetAccountCalendarMenuItem(group));
                }
            }
            else
            {
                var eligibleGroups = groups
                    .Where(group => group.AccountCalendars.Count > 0)
                    .OrderBy(group => group.Account.Order)
                    .ThenBy(group => group.Account.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(group => group.Account.Id)
                    .ToList();

                ReconcileSelectedCalendarAccount(eligibleGroups);

                foreach (var group in eligibleGroups)
                {
                    var accountMenuItem = GetCalendarAccountMenuItem(group);
                    accountMenuItem.IsSelected = group.Account.Id == _selectedCalendarAccountId;
                    desired.Add(accountMenuItem);
                }

                var selectedGroup = eligibleGroups.FirstOrDefault(group => group.Account.Id == _selectedCalendarAccountId);
                if (selectedGroup != null)
                {
                    foreach (var calendar in selectedGroup.AccountCalendars)
                    {
                        desired.Add(GetUngroupedCalendarMenuItem(calendar));
                    }
                }
            }
        }
        else
        {
            if (_hasCompletedAccountCalendarInitialization)
                ReconcileSelectedCalendarAccount([]);
        }

        ApplyDesiredMenuItems(desired);
        PruneShellMenuItemCaches();
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

    private CalendarAccountMenuItem GetCalendarAccountMenuItem(GroupedAccountCalendarViewModel group)
    {
        if (!_calendarAccountMenuItems.TryGetValue(group.Account.Id, out var menuItem))
        {
            menuItem = new CalendarAccountMenuItem(group);
            _calendarAccountMenuItems.Add(group.Account.Id, menuItem);
        }
        else
        {
            menuItem.UpdateGroup(group);
        }

        return menuItem;
    }

    private UngroupedCalendarMenuItem GetUngroupedCalendarMenuItem(AccountCalendarViewModel calendar)
    {
        if (!_ungroupedCalendarMenuItems.TryGetValue(calendar.Id, out var menuItem))
        {
            menuItem = new UngroupedCalendarMenuItem(calendar);
            _ungroupedCalendarMenuItems.Add(calendar.Id, menuItem);
        }
        else
        {
            menuItem.UpdateCalendar(calendar);
        }

        return menuItem;
    }

    private void ReconcileSelectedCalendarAccount(IReadOnlyList<GroupedAccountCalendarViewModel> eligibleGroups)
    {
        if (eligibleGroups.Count == 0)
        {
            _selectedCalendarAccountId = null;

            if (PreferencesService.CalendarStartupAccountId.HasValue)
                PreferencesService.CalendarStartupAccountId = null;

            return;
        }

        var configuredAccountId = PreferencesService.CalendarStartupAccountId;
        var configuredAccountIsEligible = configuredAccountId.HasValue
                                          && eligibleGroups.Any(group => group.Account.Id == configuredAccountId.Value);
        var selectedAccountIsEligible = _selectedCalendarAccountId.HasValue
                                        && eligibleGroups.Any(group => group.Account.Id == _selectedCalendarAccountId.Value);

        if (!selectedAccountIsEligible)
        {
            _selectedCalendarAccountId = configuredAccountIsEligible
                ? configuredAccountId
                : eligibleGroups[0].Account.Id;
        }

        if (!configuredAccountIsEligible)
            PreferencesService.CalendarStartupAccountId = eligibleGroups[0].Account.Id;
    }

    private void PruneShellMenuItemCaches()
    {
        foreach (var group in _accountCalendarMenuItems.Keys.ToList())
        {
            if (!AccountCalendarStateService.GroupedAccountCalendars.Contains(group))
                _accountCalendarMenuItems.Remove(group);
        }

        var activeAccountIds = AccountCalendarStateService.GroupedAccountCalendars
            .Where(group => group.AccountCalendars.Count > 0)
            .Select(group => group.Account.Id)
            .ToHashSet();
        foreach (var accountId in _calendarAccountMenuItems.Keys.Where(id => !activeAccountIds.Contains(id)).ToList())
        {
            _calendarAccountMenuItems[accountId].Detach();
            _calendarAccountMenuItems.Remove(accountId);
        }

        var activeCalendarIds = AccountCalendarStateService.GroupedAccountCalendars
            .SelectMany(group => group.AccountCalendars)
            .Select(calendar => calendar.Id)
            .ToHashSet();
        foreach (var calendarId in _ungroupedCalendarMenuItems.Keys.Where(id => !activeCalendarIds.Contains(id)).ToList())
        {
            _ungroupedCalendarMenuItems.Remove(calendarId);
        }
    }

    private void SelectCalendarAccount(Guid accountId)
    {
        if (_selectedCalendarAccountId == accountId
            || AccountCalendarStateService.GroupedAccountCalendars.All(group => group.Account.Id != accountId))
        {
            return;
        }

        _selectedCalendarAccountId = accountId;
        SyncShellMenuItems();
    }

    private void ClearShellMenuItemCaches()
    {
        _accountCalendarMenuItems.Clear();

        foreach (var menuItem in _calendarAccountMenuItems.Values)
        {
            menuItem.Detach();
        }

        _calendarAccountMenuItems.Clear();
        _ungroupedCalendarMenuItems.Clear();
        _selectedCalendarAccountId = null;
    }

    internal void CompleteAccountCalendarInitialization()
    {
        _hasCompletedAccountCalendarInitialization = true;
        RefreshAccountCalendarGroupSubscriptions();
        SyncShellMenuItems();
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
