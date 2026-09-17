using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Collections;
using FluentAssertions;
using Itenso.TimePeriod;
using Moq;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.PublicFolders;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.PublicFolders;

public class PublicFolderCalendarTests
{
    private readonly MailAccount _exchange = new()
    {
        Id = Guid.NewGuid(),
        Name = "Work",
        Address = "user@contoso.test",
        ProviderType = MailProviderType.Exchange,
        IsCalendarAccessGranted = true
    };

    private readonly MailAccount _outlook = new()
    {
        Id = Guid.NewGuid(),
        Name = "Home",
        ProviderType = MailProviderType.Outlook,
        IsCalendarAccessGranted = true
    };

    [Fact]
    public void Factory_BuildsAReadOnlyUnsynchronizedCalendarInTheFavouriteColour()
    {
        var favorite = PublicFolderFavorite.Create(_exchange.Id, "company", PublicFolderKind.Calendar, "Company");
        favorite.IsChecked = false;

        var calendar = PublicFolderCalendarFactory.Create(favorite);
        var again = PublicFolderCalendarFactory.Create(favorite);

        calendar.Id.Should().Be(again.Id).And.Be(PublicFolderCalendarFactory.GetCalendarId(_exchange.Id, "company"));
        calendar.AccountId.Should().Be(_exchange.Id);
        calendar.RemoteCalendarId.Should().Be("company");
        calendar.Name.Should().StartWith("Company ");
        calendar.IsPublicFolder.Should().BeTrue();
        calendar.IsReadOnly.Should().BeTrue();
        calendar.IsPrimary.Should().BeFalse();
        calendar.IsSynchronizationEnabled.Should().BeFalse();
        calendar.IsExtended.Should().BeFalse("the tick is remembered with the pin");
        calendar.BackgroundColorHex.Should().Be(favorite.ColorHex);

        ((IAccountCalendar)calendar).IsPublicFolder.Should().BeTrue();
        ((IAccountCalendar)new AccountCalendarViewModel(_exchange, calendar)).IsPublicFolder.Should().BeTrue();
        ((IAccountCalendar)new AccountCalendar()).IsPublicFolder.Should().BeFalse();
        PublicFolderCalendarFactory.Create(new PublicFolderFavorite { AccountId = _exchange.Id, FolderId = "x" })
            .BackgroundColorHex.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Page_LoadsPublicCalendarEventsLiveAndTiesThemToTheOverlay()
    {
        var own = new AccountCalendarViewModel(_exchange, OwnCalendar(_exchange));
        var overlay = new AccountCalendarViewModel(_exchange, PublicFolderCalendarFactory.Create(
            PublicFolderFavorite.Create(_exchange.Id, "company", PublicFolderKind.Calendar, "Company")));
        var ownEvent = Event("Own", new DateTime(2026, 3, 18, 9, 0, 0));
        ownEvent.CalendarId = own.Id;
        var publicEvent = Event("All hands", new DateTime(2026, 3, 19, 10, 0, 0));

        var calendarService = new Mock<ICalendarService>();
        calendarService
            .Setup(service => service.GetCalendarEventsAsync(It.Is<IAccountCalendar>(calendar => calendar.Id == own.Id), It.IsAny<ITimePeriod>()))
            .ReturnsAsync([ownEvent]);
        DateTime? requestedStart = null, requestedEnd = null;
        var publicFolders = new Mock<IPublicFolderService>();
        publicFolders
            .Setup(service => service.GetAppointmentsAsync(_exchange.Id, "company", It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, DateTime, DateTime, CancellationToken>((_, _, start, end, _) => (requestedStart, requestedEnd) = (start, end))
            .ReturnsAsync((CalendarItem[])[publicEvent]);

        var viewModel = CreatePage(calendarService.Object, publicFolders.Object, [own, overlay]);

        await viewModel.ApplyDisplayRequestAsync(new CalendarDisplayRequest(CalendarDisplayType.Week, new DateOnly(2026, 3, 18)));

        viewModel.CalendarItems.Select(item => item.Title).Should().BeEquivalentTo("Own", "All hands");
        var shown = viewModel.CalendarItems.Single(item => item.Title == "All hands");
        shown.CalendarItem.CalendarId.Should().Be(overlay.Id);
        shown.AssignedCalendar.Should().BeSameAs(overlay);
        shown.AssignedCalendar.IsReadOnly.Should().BeTrue();
        shown.CalendarItem.IsLocked.Should().BeTrue();

        requestedStart.Should().Be(viewModel.LoadedDateWindow.StartDate.ToUniversalTime());
        requestedEnd.Should().Be(viewModel.LoadedDateWindow.EndDate.ToUniversalTime());
        calendarService.Verify(
            service => service.GetCalendarEventsAsync(It.Is<IAccountCalendar>(calendar => calendar.IsPublicFolder), It.IsAny<ITimePeriod>()),
            Times.Never,
            "a public calendar has no stored events");
    }

    [Fact]
    public async Task Page_KeepsTheOtherCalendarsWhenAPublicFolderCannotBeRead()
    {
        var own = new AccountCalendarViewModel(_exchange, OwnCalendar(_exchange));
        var overlay = new AccountCalendarViewModel(_exchange, PublicFolderCalendarFactory.Create(
            PublicFolderFavorite.Create(_exchange.Id, "company", PublicFolderKind.Calendar, "Company")));
        var ownEvent = Event("Own", new DateTime(2026, 3, 18, 9, 0, 0));
        ownEvent.CalendarId = own.Id;

        var calendarService = new Mock<ICalendarService>();
        calendarService
            .Setup(service => service.GetCalendarEventsAsync(It.IsAny<IAccountCalendar>(), It.IsAny<ITimePeriod>()))
            .ReturnsAsync([ownEvent]);
        var publicFolders = new Mock<IPublicFolderService>();
        publicFolders
            .Setup(service => service.GetAppointmentsAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("public store offline"));

        var viewModel = CreatePage(calendarService.Object, publicFolders.Object, [own, overlay]);

        await viewModel.ApplyDisplayRequestAsync(new CalendarDisplayRequest(CalendarDisplayType.Week, new DateOnly(2026, 3, 18)));

        viewModel.CalendarItems.Select(item => item.Title).Should().Equal("Own");
    }

    [Fact]
    public async Task Shell_AddsPinnedCalendarsUnderTheirExchangeAccountOnly()
    {
        var favorites = new List<PublicFolderFavorite>
        {
            PublicFolderFavorite.Create(_exchange.Id, "company", PublicFolderKind.Calendar, "Company"),
            PublicFolderFavorite.Create(_exchange.Id, "staff", PublicFolderKind.Contacts, "Staff"),
            PublicFolderFavorite.Create(_outlook.Id, "other", PublicFolderKind.Calendar, "Not Exchange"),
            PublicFolderFavorite.Create(Guid.NewGuid(), "gone", PublicFolderKind.Calendar, "Removed account")
        };
        var (shell, state, _, _) = CreateShell(favorites);

        await shell.AddPublicFolderCalendarsAsync((MailAccount[])[_exchange, _outlook]);

        var overlay = state.AllCalendars.Should().ContainSingle(calendar => calendar.IsPublicFolder).Subject;
        overlay.Account.Should().BeSameAs(_exchange);
        overlay.RemoteCalendarId.Should().Be("company");
        overlay.IsChecked.Should().BeTrue();
        overlay.IsReadOnly.Should().BeTrue();
        state.GroupedAccountCalendars.Single(group => group.Account.Id == _exchange.Id).AccountCalendars.Should().HaveCount(2);
        state.GroupedAccountCalendars.Single(group => group.Account.Id == _outlook.Id).AccountCalendars.Should().HaveCount(1);
    }

    [Fact]
    public async Task Shell_RemembersTheTickWithThePinAndNeverWritesACalendarRow()
    {
        var favorites = new List<PublicFolderFavorite> { PublicFolderFavorite.Create(_exchange.Id, "company", PublicFolderKind.Calendar, "Company") };
        var (shell, state, favoriteService, calendarService) = CreateShell(favorites);
        await shell.AddPublicFolderCalendarsAsync((MailAccount[])[_exchange, _outlook]);
        var overlay = state.AllCalendars.Single(calendar => calendar.IsPublicFolder);
        var own = state.AllCalendars.First(calendar => !calendar.IsPublicFolder);

        overlay.IsChecked = false;

        shell.TryPersistPublicFolderCalendarState(overlay).Should().BeTrue();
        shell.TryPersistPublicFolderCalendarState(own).Should().BeFalse();
        shell.TryPersistPublicFolderCalendarState(null).Should().BeFalse();
        favoriteService.Verify(service => service.SetFavoriteChecked(_exchange.Id, "company", false), Times.Once);
        calendarService.Verify(service => service.UpdateAccountCalendarAsync(It.IsAny<AccountCalendar>()), Times.Never);
    }

    [Fact]
    public async Task Shell_FollowsPinsAndUnpinsMadeWhileCalendarIsOpen()
    {
        var favorites = new List<PublicFolderFavorite> { PublicFolderFavorite.Create(_exchange.Id, "company", PublicFolderKind.Calendar, "Company") };
        var (shell, state, _, _) = CreateShell(favorites);
        await shell.AddPublicFolderCalendarsAsync((MailAccount[])[_exchange, _outlook]);

        favorites.Add(PublicFolderFavorite.Create(_exchange.Id, "holidays", PublicFolderKind.Calendar, "Holidays"));
        await shell.ReconcilePublicFolderCalendarsAsync(_exchange.Id);

        state.AllCalendars.Where(calendar => calendar.IsPublicFolder).Select(calendar => calendar.RemoteCalendarId)
            .Should().BeEquivalentTo("company", "holidays");

        favorites.RemoveAll(favorite => favorite.FolderId == "company");
        await shell.ReconcilePublicFolderCalendarsAsync(_exchange.Id);

        state.AllCalendars.Where(calendar => calendar.IsPublicFolder).Select(calendar => calendar.RemoteCalendarId)
            .Should().Equal("holidays");
        state.AllCalendars.Count(calendar => !calendar.IsPublicFolder).Should().Be(2, "stored calendars are never touched");
    }

    private (CalendarAppShellViewModel Shell, FakeStateService State, Mock<IPublicFolderFavoriteService> Favorites, Mock<ICalendarService> CalendarService) CreateShell(List<PublicFolderFavorite> favorites)
    {
        var state = new FakeStateService(
        [
            new GroupedAccountCalendarViewModel(_exchange, [new AccountCalendarViewModel(_exchange, OwnCalendar(_exchange))]),
            new GroupedAccountCalendarViewModel(_outlook, [new AccountCalendarViewModel(_outlook, OwnCalendar(_outlook))])
        ]);
        var favoriteService = new Mock<IPublicFolderFavoriteService>();
        favoriteService.Setup(service => service.GetFavorites()).Returns(() => favorites.ToList());
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountsAsync()).ReturnsAsync([_exchange, _outlook]);
        var calendarService = new Mock<ICalendarService>();
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupProperty(service => service.IsCalendarAccountsGrouped, true);

        var shell = new CalendarAppShellViewModel(
            preferences.Object,
            Mock.Of<IStatePersistanceService>(),
            accountService.Object,
            calendarService.Object,
            state,
            Mock.Of<INavigationService>(),
            new Lazy<CalendarPageViewModel>(() => null!),
            Mock.Of<IMailDialogService>(),
            Mock.Of<IDateContextProvider>(),
            favoriteService.Object)
        {
            Dispatcher = new ImmediateDispatcher()
        };

        return (shell, state, favoriteService, calendarService);
    }

    private static CalendarPageViewModel CreatePage(ICalendarService calendarService, IPublicFolderService publicFolders, IEnumerable<AccountCalendarViewModel> calendars)
    {
        var statePersistence = new Mock<IStatePersistanceService>();
        statePersistence.SetupAllProperties();
        statePersistence.Object.ApplicationMode = WinoApplicationMode.Calendar;
        statePersistence.Object.CalendarDisplayType = CalendarDisplayType.Week;

        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(service => service.GetCurrentCalendarSettings()).Returns(new CalendarSettings(
            DayOfWeek.Monday,
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            true,
            DayOfWeek.Monday,
            DayOfWeek.Friday,
            TimeSpan.FromHours(9),
            TimeSpan.FromHours(18),
            64,
            DayHeaderDisplayType.TwentyFourHour,
            CultureInfo.GetCultureInfo("en-US")));

        var groups = calendars.GroupBy(calendar => calendar.Account).Select(group => new GroupedAccountCalendarViewModel(group.Key, group));

        return new CalendarPageViewModel(
            statePersistence.Object,
            calendarService,
            Mock.Of<INavigationService>(),
            Mock.Of<IKeyPressService>(),
            Mock.Of<INativeAppService>(),
            new FakeStateService(groups),
            Mock.Of<INotificationBuilder>(),
            preferences.Object,
            Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<IMailDialogService>(),
            new FixedDateContext(new DateOnly(2026, 3, 20)),
            new CalendarRangeTextFormatter(),
            Mock.Of<ICalendarShellClient>(),
            publicFolders);
    }

    private static AccountCalendar OwnCalendar(MailAccount account)
        => new()
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Name = "Calendar",
            RemoteCalendarId = "calendar",
            IsExtended = true,
            IsPrimary = true
        };

    private static CalendarItem Event(string title, DateTime start)
        => new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            StartDate = start,
            DurationInSeconds = TimeSpan.FromMinutes(30).TotalSeconds
        };

    private sealed class ImmediateDispatcher : IDispatcher
    {
        public Task ExecuteOnUIThread(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class FixedDateContext(DateOnly today) : IDateContextProvider
    {
        public CultureInfo Culture => CultureInfo.GetCultureInfo("en-US");
        public TimeZoneInfo TimeZone => TimeZoneInfo.Utc;
        public DateOnly GetToday() => today;
    }

    private sealed class FakeStateService : IAccountCalendarStateService
    {
        private readonly ObservableCollection<GroupedAccountCalendarViewModel> _groups;

        public FakeStateService(IEnumerable<GroupedAccountCalendarViewModel> groups)
        {
            _groups = new ObservableCollection<GroupedAccountCalendarViewModel>(groups);
            GroupedAccountCalendars = new ReadOnlyObservableCollection<GroupedAccountCalendarViewModel>(_groups);
        }

        public IDispatcher Dispatcher { get; set; } = null!;
        public ReadOnlyObservableCollection<GroupedAccountCalendarViewModel> GroupedAccountCalendars { get; }
        public ReadOnlyObservableGroupedCollection<MailAccount, AccountCalendarViewModel> GroupedCalendars { get; set; } = null!;
        public IEnumerable<AccountCalendarViewModel> ActiveCalendars => AllCalendars.Where(calendar => calendar.IsChecked).ToList();
        public IEnumerable<AccountCalendarViewModel> AllCalendars => _groups.SelectMany(group => group.AccountCalendars).ToList();
        public bool IsAnySynchronizationInProgress => false;

        public event EventHandler<GroupedAccountCalendarViewModel>? CollectiveAccountGroupSelectionStateChanged { add { } remove { } }
        public event EventHandler<AccountCalendarViewModel>? AccountCalendarSelectionStateChanged { add { } remove { } }
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

        public void AddGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar) => _groups.Add(groupedAccountCalendar);
        public void RemoveGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar) => _groups.Remove(groupedAccountCalendar);
        public void ClearGroupedAccountCalendars() => _groups.Clear();

        public void AddAccountCalendar(AccountCalendarViewModel accountCalendar)
            => _groups.Single(group => group.Account.Id == accountCalendar.Account.Id).AccountCalendars.Add(accountCalendar);

        public void RemoveAccountCalendar(AccountCalendarViewModel accountCalendar)
            => _groups.Single(group => group.Account.Id == accountCalendar.Account.Id).AccountCalendars.Remove(accountCalendar);
    }
}
