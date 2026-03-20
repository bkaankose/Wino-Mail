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
using Xunit;

namespace Wino.Core.Tests;

public class CalendarPageViewModelTests
{
    [Fact]
    public async Task ApplyDisplayRequestAsync_UpdatesVisibleRangeAndThreePeriodLoadWindow()
    {
        var settings = CreateSettings(firstDayOfWeek: DayOfWeek.Monday);
        var today = new DateOnly(2026, 3, 20);
        var preferencesService = CreatePreferencesService(settings);
        var calendarService = new Mock<ICalendarService>();
        ITimePeriod? requestedPeriod = null;

        calendarService
            .Setup(service => service.GetCalendarEventsAsync(It.IsAny<IAccountCalendar>(), It.IsAny<ITimePeriod>()))
            .Callback<IAccountCalendar, ITimePeriod>((_, period) => requestedPeriod = period)
            .ReturnsAsync([]);

        var viewModel = CreateViewModel(calendarService.Object, preferencesService.Object, today);
        var request = new CalendarDisplayRequest(CalendarDisplayType.Week, new DateOnly(2026, 3, 18));

        await viewModel.ApplyDisplayRequestAsync(request);

        viewModel.CurrentVisibleRange.StartDate.Should().Be(new DateOnly(2026, 3, 16));
        viewModel.CurrentVisibleRange.EndDate.Should().Be(new DateOnly(2026, 3, 22));
        viewModel.LoadedDateWindow.StartDate.Should().Be(new DateTime(2026, 3, 9));
        viewModel.LoadedDateWindow.EndDate.Should().Be(new DateTime(2026, 3, 30));
        viewModel.VisibleDateRangeText.Should().Be("3/16/2026 - 3/22/2026");

        requestedPeriod.Should().NotBeNull();
        requestedPeriod!.Start.Should().Be(new DateTime(2026, 3, 9));
        requestedPeriod.End.Should().Be(new DateTime(2026, 3, 30));
        calendarService.Verify(service => service.GetCalendarEventsAsync(It.IsAny<IAccountCalendar>(), It.IsAny<ITimePeriod>()), Times.Once);
    }

    [Fact]
    public async Task ApplyDisplayRequestAsync_DoesNotReloadWhenResolvedRangeIsUnchanged()
    {
        var settings = CreateSettings();
        var preferencesService = CreatePreferencesService(settings);
        var calendarService = new Mock<ICalendarService>();

        calendarService
            .Setup(service => service.GetCalendarEventsAsync(It.IsAny<IAccountCalendar>(), It.IsAny<ITimePeriod>()))
            .ReturnsAsync([]);

        var viewModel = CreateViewModel(calendarService.Object, preferencesService.Object, new DateOnly(2026, 3, 20));
        var request = new CalendarDisplayRequest(CalendarDisplayType.Day, new DateOnly(2026, 3, 20));

        await viewModel.ApplyDisplayRequestAsync(request);
        await viewModel.ApplyDisplayRequestAsync(request);

        calendarService.Verify(service => service.GetCalendarEventsAsync(It.IsAny<IAccountCalendar>(), It.IsAny<ITimePeriod>()), Times.Once);
    }

    [Fact]
    public async Task ReloadCurrentVisibleRangeAsync_RecomputesWhenCalendarSettingsChange()
    {
        var currentSettings = CreateSettings(firstDayOfWeek: DayOfWeek.Monday);
        var preferencesService = CreatePreferencesService(() => currentSettings);
        var calendarService = new Mock<ICalendarService>();

        calendarService
            .Setup(service => service.GetCalendarEventsAsync(It.IsAny<IAccountCalendar>(), It.IsAny<ITimePeriod>()))
            .ReturnsAsync([]);

        var viewModel = CreateViewModel(calendarService.Object, preferencesService.Object, new DateOnly(2026, 3, 20));
        var request = new CalendarDisplayRequest(CalendarDisplayType.Week, new DateOnly(2026, 3, 18));

        await viewModel.ApplyDisplayRequestAsync(request);
        viewModel.CurrentVisibleRange.StartDate.Should().Be(new DateOnly(2026, 3, 16));

        currentSettings = CreateSettings(firstDayOfWeek: DayOfWeek.Sunday);
        await viewModel.ReloadCurrentVisibleRangeAsync();

        viewModel.CurrentVisibleRange.StartDate.Should().Be(new DateOnly(2026, 3, 15));
        calendarService.Verify(service => service.GetCalendarEventsAsync(It.IsAny<IAccountCalendar>(), It.IsAny<ITimePeriod>()), Times.Exactly(2));
    }

    private static CalendarPageViewModel CreateViewModel(
        ICalendarService calendarService,
        IPreferencesService preferencesService,
        DateOnly today)
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Primary",
            SenderName = "Primary",
            Address = "primary@example.com",
            ProviderType = MailProviderType.Outlook
        };

        var calendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Name = "Calendar",
            RemoteCalendarId = "calendar",
            SynchronizationDeltaToken = string.Empty,
            TextColorHex = "#000000",
            BackgroundColorHex = "#ffffff",
            TimeZone = TimeZoneInfo.Utc.Id,
            IsExtended = true,
            IsPrimary = true,
            IsSynchronizationEnabled = true
        };

        var accountCalendarViewModel = new AccountCalendarViewModel(account, calendar);
        var accountCalendarStateService = new FakeAccountCalendarStateService([accountCalendarViewModel]);

        var statePersistenceService = new Mock<IStatePersistanceService>();
        statePersistenceService.SetupAllProperties();
        statePersistenceService.Object.ApplicationMode = WinoApplicationMode.Calendar;
        statePersistenceService.Object.CalendarDisplayType = CalendarDisplayType.Week;

        return new CalendarPageViewModel(
            statePersistenceService.Object,
            calendarService,
            Mock.Of<INavigationService>(),
            Mock.Of<IKeyPressService>(),
            Mock.Of<INativeAppService>(),
            accountCalendarStateService,
            preferencesService,
            Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<IMailDialogService>(),
            new TestDateContextProvider("en-US", today),
            new CalendarRangeTextFormatter());
    }

    private static Mock<IPreferencesService> CreatePreferencesService(CalendarSettings settings)
        => CreatePreferencesService(() => settings);

    private static Mock<IPreferencesService> CreatePreferencesService(Func<CalendarSettings> settingsFactory)
    {
        var preferencesService = new Mock<IPreferencesService>();
        preferencesService.Setup(service => service.GetCurrentCalendarSettings()).Returns(settingsFactory);
        return preferencesService;
    }

    private static CalendarSettings CreateSettings(
        DayOfWeek firstDayOfWeek = DayOfWeek.Monday,
        DayOfWeek workWeekStart = DayOfWeek.Monday,
        DayOfWeek workWeekEnd = DayOfWeek.Friday,
        string cultureName = "en-US")
    {
        return new CalendarSettings(
            firstDayOfWeek,
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            workWeekStart,
            workWeekEnd,
            TimeSpan.FromHours(8),
            TimeSpan.FromHours(17),
            64,
            DayHeaderDisplayType.TwentyFourHour,
            CultureInfo.GetCultureInfo(cultureName));
    }

    private sealed class FakeAccountCalendarStateService : IAccountCalendarStateService
    {
        private readonly List<AccountCalendarViewModel> _calendars;
        private readonly ObservableCollection<GroupedAccountCalendarViewModel> _groupedCalendars = [];

        public FakeAccountCalendarStateService(IEnumerable<AccountCalendarViewModel> calendars)
        {
            _calendars = calendars.ToList();
            GroupedAccountCalendars = new ReadOnlyObservableCollection<GroupedAccountCalendarViewModel>(_groupedCalendars);
        }

        public IDispatcher Dispatcher { get; set; } = null!;
        public ReadOnlyObservableCollection<GroupedAccountCalendarViewModel> GroupedAccountCalendars { get; }

        public event EventHandler<GroupedAccountCalendarViewModel>? CollectiveAccountGroupSelectionStateChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<AccountCalendarViewModel>? AccountCalendarSelectionStateChanged
        {
            add { }
            remove { }
        }

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public IEnumerable<AccountCalendarViewModel> ActiveCalendars => _calendars;
        public IEnumerable<AccountCalendarViewModel> AllCalendars => _calendars;
        public ReadOnlyObservableGroupedCollection<MailAccount, AccountCalendarViewModel> GroupedCalendars { get; set; } = null!;

        public void AddGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar) => _groupedCalendars.Add(groupedAccountCalendar);
        public void RemoveGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar) => _groupedCalendars.Remove(groupedAccountCalendar);
        public void ClearGroupedAccountCalendars() => _groupedCalendars.Clear();
        public void AddAccountCalendar(AccountCalendarViewModel accountCalendar) => _calendars.Add(accountCalendar);
        public void RemoveAccountCalendar(AccountCalendarViewModel accountCalendar) => _calendars.Remove(accountCalendar);
    }

    private sealed class TestDateContextProvider(string cultureName, DateOnly today) : IDateContextProvider
    {
        public CultureInfo Culture => CultureInfo.GetCultureInfo(cultureName);
        public TimeZoneInfo TimeZone => TimeZoneInfo.Utc;
        public DateOnly GetToday() => today;
    }
}
