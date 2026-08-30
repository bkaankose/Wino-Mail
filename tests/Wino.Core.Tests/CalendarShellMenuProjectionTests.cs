using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Collections;
using FluentAssertions;
using Moq;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Core.Tests;

public sealed class CalendarShellMenuProjectionTests
{
    [Fact]
    public void GroupedProjection_PreservesExistingGroupRows()
    {
        var first = CreateGroup("First", 2, "First calendar");
        var second = CreateGroup("Second", 1, "Second calendar");
        var preferences = CreatePreferences(grouped: true, startupAccountId: null);
        var viewModel = CreateViewModel(preferences.Object, new FakeAccountCalendarStateService([first, second]));

        viewModel.ShellMenu.Items.Select(item => item.GetType()).Should().ContainInOrder(
            typeof(NewCalendarEventMenuItem),
            typeof(CalendarDatePickerMenuItem),
            typeof(ShellSectionHeaderMenuItem),
            typeof(AccountCalendarGroupMenuItem),
            typeof(AccountCalendarGroupMenuItem));
        viewModel.ShellMenu.Items[3].Should().BeOfType<AccountCalendarGroupMenuItem>().Which.Parameter.Should().Be(first);
        viewModel.ShellMenu.Items[4].Should().BeOfType<AccountCalendarGroupMenuItem>().Which.Parameter.Should().Be(second);
        viewModel.ShellMenu.Items.Where(item => item is CalendarAccountMenuItem or UngroupedCalendarMenuItem).Should().BeEmpty();
    }

    [Fact]
    public void UngroupedProjection_OrdersAccountsThenShowsOnlyStartupAccountCalendars()
    {
        var later = CreateGroup("Later", 2, "Later calendar");
        var startup = CreateGroup("Startup", 1, "Startup calendar A", "Startup calendar B");
        var preferences = CreatePreferences(grouped: false, startup.Account.Id);
        var viewModel = CreateViewModel(preferences.Object, new FakeAccountCalendarStateService([later, startup]));

        var projected = viewModel.ShellMenu.Items.Skip(3).ToList();
        projected.Should().HaveCount(4);
        projected[0].Should().BeOfType<CalendarAccountMenuItem>().Which.Account.Should().Be(startup.Account);
        projected[1].Should().BeOfType<CalendarAccountMenuItem>().Which.Account.Should().Be(later.Account);
        projected[0].IsSelected.Should().BeTrue();
        projected[1].IsSelected.Should().BeFalse();
        projected[2].Should().BeOfType<UngroupedCalendarMenuItem>().Which.Parameter.Name.Should().Be("Startup calendar A");
        projected[3].Should().BeOfType<UngroupedCalendarMenuItem>().Which.Parameter.Name.Should().Be("Startup calendar B");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UngroupedProjection_MissingOrInvalidStartup_RepairsToFirstEligibleAccount(bool useInvalidId)
    {
        var first = CreateGroup("First", 1, "First calendar");
        var second = CreateGroup("Second", 2, "Second calendar");
        var configuredId = useInvalidId ? Guid.NewGuid() : (Guid?)null;
        var preferences = CreatePreferences(grouped: false, configuredId);

        var viewModel = CreateViewModel(preferences.Object, new FakeAccountCalendarStateService([second, first]));

        preferences.Object.CalendarStartupAccountId.Should().Be(first.Account.Id);
        viewModel.ShellMenu.Items.OfType<CalendarAccountMenuItem>().Single(item => item.IsSelected).Account.Should().Be(first.Account);
    }

    [Fact]
    public void UngroupedProjection_NoEligibleAccounts_ClearsStartupOnlyAfterInitializationCompletes()
    {
        var startupAccountId = Guid.NewGuid();
        var preferences = CreatePreferences(grouped: false, startupAccountId);

        var viewModel = CreateViewModel(preferences.Object, new FakeAccountCalendarStateService([]));

        preferences.Object.CalendarStartupAccountId.Should().Be(startupAccountId);

        viewModel.CompleteAccountCalendarInitialization();

        preferences.Object.CalendarStartupAccountId.Should().BeNull();
        viewModel.ShellMenu.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task SwitchingAccount_ChangesCalendarRowsWithoutChangingStartupPreference()
    {
        var first = CreateGroup("First", 1, "First calendar");
        var second = CreateGroup("Second", 2, "Second calendar");
        var preferences = CreatePreferences(grouped: false, first.Account.Id);
        var viewModel = CreateViewModel(preferences.Object, new FakeAccountCalendarStateService([first, second]));
        var firstCalendarItem = viewModel.ShellMenu.Items.OfType<UngroupedCalendarMenuItem>().Single();
        var secondAccountItem = viewModel.ShellMenu.Items.OfType<CalendarAccountMenuItem>().Single(item => item.Account == second.Account);

        await viewModel.OnMenuItemInvokedAsync(secondAccountItem);

        preferences.Object.CalendarStartupAccountId.Should().Be(first.Account.Id);
        viewModel.ShellMenu.Items.OfType<CalendarAccountMenuItem>().Single(item => item.IsSelected).Should().BeSameAs(secondAccountItem);
        viewModel.ShellMenu.Items.OfType<UngroupedCalendarMenuItem>().Single().Parameter.Name.Should().Be("Second calendar");
        viewModel.ShellMenu.Items.Should().NotContain(firstCalendarItem);
    }

    [Fact]
    public async Task CalendarRowInvocation_TogglesExactlyOnceAndRaisesOnePersistenceRequest()
    {
        var group = CreateGroup("First", 1, "Calendar");
        var preferences = CreatePreferences(grouped: false, group.Account.Id);
        var state = new FakeAccountCalendarStateService([group]);
        var persistenceRequests = 0;
        state.AccountCalendarSelectionStateChanged += (_, _) => persistenceRequests++;
        var viewModel = CreateViewModel(preferences.Object, state);
        var calendarItem = viewModel.ShellMenu.Items.OfType<UngroupedCalendarMenuItem>().Single();
        var initialValue = calendarItem.Parameter.IsChecked;

        await viewModel.OnMenuItemInvokedAsync(calendarItem);

        calendarItem.Parameter.IsChecked.Should().Be(!initialValue);
        persistenceRequests.Should().Be(1);
    }

    [Fact]
    public async Task GroupingPreferenceChange_RebuildsProjectionAndReusesUnchangedAccountItem()
    {
        var group = CreateGroup("First", 1, "Calendar");
        var preferences = CreatePreferences(grouped: false, group.Account.Id);
        var viewModel = CreateViewModel(preferences.Object, new FakeAccountCalendarStateService([group]));
        var accountItem = viewModel.ShellMenu.Items.OfType<CalendarAccountMenuItem>().Single();

        preferences.Object.IsCalendarAccountsGrouped = true;
        await viewModel.ApplyCalendarGroupingPreferenceChangeAsync();
        viewModel.ShellMenu.Items.Should().ContainSingle(item => item is AccountCalendarGroupMenuItem);

        preferences.Object.IsCalendarAccountsGrouped = false;
        await viewModel.ApplyCalendarGroupingPreferenceChangeAsync();
        viewModel.ShellMenu.Items.OfType<CalendarAccountMenuItem>().Single().Should().BeSameAs(accountItem);
    }

    [Fact]
    public void RemovedStartupAccount_FallsBackAndRepairsPreference()
    {
        var first = CreateGroup("First", 1, "First calendar");
        var removed = CreateGroup("Removed", 2, "Removed calendar");
        var preferences = CreatePreferences(grouped: false, removed.Account.Id);
        var state = new FakeAccountCalendarStateService([first, removed]);
        var viewModel = CreateViewModel(preferences.Object, state);

        state.RemoveGroupedAccountCalendar(removed);
        viewModel.SetPaneCompact(true);
        viewModel.SetPaneCompact(false);

        preferences.Object.CalendarStartupAccountId.Should().Be(first.Account.Id);
        viewModel.ShellMenu.Items.OfType<CalendarAccountMenuItem>().Single().IsSelected.Should().BeTrue();
        viewModel.ShellMenu.Items.OfType<UngroupedCalendarMenuItem>().Single().Parameter.Name.Should().Be("First calendar");
    }

    [Fact]
    public void DatePickerExpansion_InitializesAndPersistsWithDynamicAccessibilityText()
    {
        var preferences = CreatePreferences(grouped: true, startupAccountId: null, datePickerExpanded: true);
        var item = new CalendarDatePickerMenuItem(Mock.Of<ICalendarShellClient>(), preferences.Object);

        item.IsCalendarExpanded.Should().BeTrue();
        item.ExpansionAutomationName.Should().Contain("Collapse");
        item.ExpansionGlyph.Should().Be("\uE70E");

        item.IsCalendarExpanded = false;

        preferences.Object.IsCalendarDatePickerExpanded.Should().BeFalse();
        item.ExpansionAutomationName.Should().Contain("Expand");
        item.ExpansionGlyph.Should().Be("\uE70D");
    }

    private static CalendarAppShellViewModel CreateViewModel(
        IPreferencesService preferences,
        IAccountCalendarStateService state)
    {
        var viewModel = new CalendarAppShellViewModel(
            preferences,
            Mock.Of<IStatePersistanceService>(),
            Mock.Of<IAccountService>(),
            Mock.Of<ICalendarService>(),
            state,
            Mock.Of<INavigationService>(),
            new Lazy<CalendarPageViewModel>(() => null!),
            Mock.Of<IMailDialogService>(),
            Mock.Of<IDateContextProvider>());
        viewModel.Dispatcher = new ImmediateDispatcher();
        return viewModel;
    }

    private static Mock<IPreferencesService> CreatePreferences(
        bool grouped,
        Guid? startupAccountId,
        bool datePickerExpanded = true)
    {
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupProperty(service => service.IsCalendarAccountsGrouped, grouped);
        preferences.SetupProperty(service => service.CalendarStartupAccountId, startupAccountId);
        preferences.SetupProperty(service => service.IsCalendarDatePickerExpanded, datePickerExpanded);
        return preferences;
    }

    private static GroupedAccountCalendarViewModel CreateGroup(string name, int order, params string[] calendarNames)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = name,
            Address = $"{name.ToLowerInvariant()}@example.com",
            Order = order,
            IsCalendarAccessGranted = true
        };
        var calendars = calendarNames.Select(calendarName => new AccountCalendarViewModel(
            account,
            new AccountCalendar
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id,
                Name = calendarName,
                IsExtended = true
            })).ToList();
        return new GroupedAccountCalendarViewModel(account, calendars);
    }

    private sealed class ImmediateDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAccountCalendarStateService : IAccountCalendarStateService
    {
        private readonly ObservableCollection<GroupedAccountCalendarViewModel> _groups;

        public FakeAccountCalendarStateService(IEnumerable<GroupedAccountCalendarViewModel> groups)
        {
            _groups = new ObservableCollection<GroupedAccountCalendarViewModel>(groups);
            GroupedAccountCalendars = new ReadOnlyObservableCollection<GroupedAccountCalendarViewModel>(_groups);

            foreach (var group in _groups)
            {
                Attach(group);
            }
        }

        public IDispatcher Dispatcher { get; set; } = null!;
        public ReadOnlyObservableCollection<GroupedAccountCalendarViewModel> GroupedAccountCalendars { get; }
        public ReadOnlyObservableGroupedCollection<MailAccount, AccountCalendarViewModel> GroupedCalendars { get; set; } = null!;
        public IEnumerable<AccountCalendarViewModel> ActiveCalendars => AllCalendars.Where(calendar => calendar.IsChecked);
        public IEnumerable<AccountCalendarViewModel> AllCalendars => _groups.SelectMany(group => group.AccountCalendars);
        public bool IsAnySynchronizationInProgress => false;

        public event EventHandler<GroupedAccountCalendarViewModel>? CollectiveAccountGroupSelectionStateChanged;
        public event EventHandler<AccountCalendarViewModel>? AccountCalendarSelectionStateChanged;
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public void AddGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar)
        {
            Attach(groupedAccountCalendar);
            _groups.Add(groupedAccountCalendar);
        }

        public void RemoveGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar)
        {
            Detach(groupedAccountCalendar);
            _groups.Remove(groupedAccountCalendar);
        }

        public void ClearGroupedAccountCalendars()
        {
            foreach (var group in _groups.ToList())
            {
                RemoveGroupedAccountCalendar(group);
            }
        }

        public void AddAccountCalendar(AccountCalendarViewModel accountCalendar)
            => _groups.Single(group => group.Account.Id == accountCalendar.Account.Id).AccountCalendars.Add(accountCalendar);

        public void RemoveAccountCalendar(AccountCalendarViewModel accountCalendar)
            => _groups.Single(group => group.Account.Id == accountCalendar.Account.Id).AccountCalendars.Remove(accountCalendar);

        private void Attach(GroupedAccountCalendarViewModel group)
        {
            group.CollectiveSelectionStateChanged += GroupCollectiveSelectionStateChanged;
            group.CalendarSelectionStateChanged += GroupCalendarSelectionStateChanged;
        }

        private void Detach(GroupedAccountCalendarViewModel group)
        {
            group.CollectiveSelectionStateChanged -= GroupCollectiveSelectionStateChanged;
            group.CalendarSelectionStateChanged -= GroupCalendarSelectionStateChanged;
        }

        private void GroupCollectiveSelectionStateChanged(object? sender, EventArgs e)
            => CollectiveAccountGroupSelectionStateChanged?.Invoke(this, (GroupedAccountCalendarViewModel)sender!);

        private void GroupCalendarSelectionStateChanged(object? sender, AccountCalendarViewModel calendar)
            => AccountCalendarSelectionStateChanged?.Invoke(this, calendar);
    }
}
