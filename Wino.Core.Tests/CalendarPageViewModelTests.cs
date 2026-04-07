using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Collections;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Itenso.TimePeriod;
using Moq;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Messaging.Client.Calendar;
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
        viewModel.VisibleDateRangeText.Should().Be("March 16 - March 22");

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

    [Fact]
    public async Task ApplyDisplayRequestAsync_LoadsOnlyActiveCalendars()
    {
        var settings = CreateSettings();
        var preferencesService = CreatePreferencesService(settings);
        var calendarService = new Mock<ICalendarService>();

        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Primary",
            SenderName = "Primary",
            Address = "primary@example.com",
            ProviderType = MailProviderType.Outlook
        };

        var visibleCalendar = CreateCalendar(account, "Visible calendar");
        var hiddenCalendar = CreateCalendar(account, "Hidden calendar");
        var visibleCalendarViewModel = new AccountCalendarViewModel(account, visibleCalendar);
        var hiddenCalendarViewModel = new AccountCalendarViewModel(account, hiddenCalendar);
        hiddenCalendarViewModel.IsChecked = false;

        calendarService
            .Setup(service => service.GetCalendarEventsAsync(It.Is<IAccountCalendar>(calendar => calendar.Id == visibleCalendar.Id), It.IsAny<ITimePeriod>()))
            .ReturnsAsync([
                new CalendarItem
                {
                    Id = Guid.NewGuid(),
                    CalendarId = visibleCalendar.Id,
                    StartDate = new DateTime(2026, 3, 20, 9, 0, 0),
                    DurationInSeconds = TimeSpan.FromMinutes(30).TotalSeconds,
                    Title = "Visible event"
                }
            ]);

        calendarService
            .Setup(service => service.GetCalendarEventsAsync(It.Is<IAccountCalendar>(calendar => calendar.Id == hiddenCalendar.Id), It.IsAny<ITimePeriod>()))
            .ReturnsAsync([
                new CalendarItem
                {
                    Id = Guid.NewGuid(),
                    CalendarId = hiddenCalendar.Id,
                    StartDate = new DateTime(2026, 3, 20, 10, 0, 0),
                    DurationInSeconds = TimeSpan.FromMinutes(30).TotalSeconds,
                    Title = "Hidden event"
                }
            ]);

        var accountCalendarStateService = new FakeAccountCalendarStateService(
            [visibleCalendarViewModel, hiddenCalendarViewModel],
            [visibleCalendarViewModel]);

        var viewModel = CreateViewModel(calendarService.Object, preferencesService.Object, new DateOnly(2026, 3, 20), accountCalendarStateService);

        await viewModel.ApplyDisplayRequestAsync(new CalendarDisplayRequest(CalendarDisplayType.Day, new DateOnly(2026, 3, 20)));

        viewModel.CalendarItems.Should().ContainSingle(item => item.CalendarItem.CalendarId == visibleCalendar.Id);
        calendarService.Verify(service => service.GetCalendarEventsAsync(It.Is<IAccountCalendar>(calendar => calendar.Id == visibleCalendar.Id), It.IsAny<ITimePeriod>()), Times.Once);
        calendarService.Verify(service => service.GetCalendarEventsAsync(It.Is<IAccountCalendar>(calendar => calendar.Id == hiddenCalendar.Id), It.IsAny<ITimePeriod>()), Times.Never);
    }

    [Fact]
    public async Task CalendarItemAddedMessage_AddsVisibleItemWithoutReloadAndMarksBusy()
    {
        var settings = CreateSettings();
        var preferencesService = CreatePreferencesService(settings);
        var calendarService = new Mock<ICalendarService>();

        var account = CreateAccount();
        var calendar = CreateCalendar(account, "Calendar");
        var accountCalendarViewModel = new AccountCalendarViewModel(account, calendar);
        var existingItem = CreateCalendarItem(calendar.Id, new DateTime(2026, 3, 20, 9, 0, 0), "Existing");

        calendarService
            .Setup(service => service.GetCalendarEventsAsync(It.IsAny<IAccountCalendar>(), It.IsAny<ITimePeriod>()))
            .ReturnsAsync([existingItem]);

        var viewModel = CreateViewModel(
            calendarService.Object,
            preferencesService.Object,
            new DateOnly(2026, 3, 20),
            new FakeAccountCalendarStateService([accountCalendarViewModel]));

        viewModel.OnNavigatedTo(NavigationMode.New, null!);

        try
        {
            await viewModel.ApplyDisplayRequestAsync(new CalendarDisplayRequest(CalendarDisplayType.Day, new DateOnly(2026, 3, 20)));

            var optimisticItem = CreateCalendarItem(calendar.Id, new DateTime(2026, 3, 20, 10, 0, 0), "Optimistic");
            optimisticItem.AssignedCalendar = accountCalendarViewModel;

            WeakReferenceMessenger.Default.Send(new CalendarItemAdded(optimisticItem, EntityUpdateSource.ClientUpdated));

            viewModel.CalendarItems.Should().HaveCount(2);
            viewModel.CalendarItems.Should().Contain(item => item.Id == optimisticItem.Id && item.IsBusy);
            calendarService.Verify(service => service.GetCalendarEventsAsync(It.IsAny<IAccountCalendar>(), It.IsAny<ITimePeriod>()), Times.Once);
        }
        finally
        {
            viewModel.OnNavigatedFrom(NavigationMode.Back, null!);
        }
    }

    [Fact]
    public async Task CalendarItemDeletedMessage_RemovesVisibleItemWithoutReload()
    {
        var settings = CreateSettings();
        var preferencesService = CreatePreferencesService(settings);
        var calendarService = new Mock<ICalendarService>();

        var account = CreateAccount();
        var calendar = CreateCalendar(account, "Calendar");
        var accountCalendarViewModel = new AccountCalendarViewModel(account, calendar);
        var existingItem = CreateCalendarItem(calendar.Id, new DateTime(2026, 3, 20, 9, 0, 0), "Existing");

        calendarService
            .Setup(service => service.GetCalendarEventsAsync(It.IsAny<IAccountCalendar>(), It.IsAny<ITimePeriod>()))
            .ReturnsAsync([existingItem]);

        var viewModel = CreateViewModel(
            calendarService.Object,
            preferencesService.Object,
            new DateOnly(2026, 3, 20),
            new FakeAccountCalendarStateService([accountCalendarViewModel]));

        viewModel.OnNavigatedTo(NavigationMode.New, null!);

        try
        {
            await viewModel.ApplyDisplayRequestAsync(new CalendarDisplayRequest(CalendarDisplayType.Day, new DateOnly(2026, 3, 20)));

            WeakReferenceMessenger.Default.Send(new CalendarItemDeleted(existingItem, EntityUpdateSource.ClientUpdated));

            viewModel.CalendarItems.Should().BeEmpty();
            calendarService.Verify(service => service.GetCalendarEventsAsync(It.IsAny<IAccountCalendar>(), It.IsAny<ITimePeriod>()), Times.Once);
        }
        finally
        {
            viewModel.OnNavigatedFrom(NavigationMode.Back, null!);
        }
    }

    [Fact]
    public async Task CalendarItemAddedMessage_ReconcilesTrackedLocalPreviewInPlace()
    {
        var settings = CreateSettings();
        var preferencesService = CreatePreferencesService(settings);
        var calendarService = new Mock<ICalendarService>();

        var account = CreateAccount();
        var calendar = CreateCalendar(account, "Calendar");
        var accountCalendarViewModel = new AccountCalendarViewModel(account, calendar);
        var localPreview = CreateCalendarItem(calendar.Id, new DateTime(2026, 3, 20, 9, 0, 0), "Local preview");

        calendarService
            .Setup(service => service.GetCalendarEventsAsync(It.IsAny<IAccountCalendar>(), It.IsAny<ITimePeriod>()))
            .ReturnsAsync([localPreview]);

        var viewModel = CreateViewModel(
            calendarService.Object,
            preferencesService.Object,
            new DateOnly(2026, 3, 20),
            new FakeAccountCalendarStateService([accountCalendarViewModel]));

        viewModel.OnNavigatedTo(NavigationMode.New, null!);

        try
        {
            await viewModel.ApplyDisplayRequestAsync(new CalendarDisplayRequest(CalendarDisplayType.Day, new DateOnly(2026, 3, 20)));

            var syncedItem = CreateCalendarItem(calendar.Id, localPreview.StartDate, "Synced");
            syncedItem.RemoteEventId = "remote-event-id".WithClientTrackingId(localPreview.Id);
            syncedItem.AssignedCalendar = accountCalendarViewModel;

            WeakReferenceMessenger.Default.Send(new CalendarItemAdded(syncedItem, EntityUpdateSource.Server));

            viewModel.CalendarItems.Should().ContainSingle();
            viewModel.CalendarItems[0].Id.Should().Be(syncedItem.Id);
            viewModel.CalendarItems[0].Title.Should().Be("Synced");
            viewModel.CalendarItems[0].IsBusy.Should().BeFalse();
            viewModel.CalendarItems.Should().NotContain(item => item.Id == localPreview.Id);
        }
        finally
        {
            viewModel.OnNavigatedFrom(NavigationMode.Back, null!);
        }
    }

    private static CalendarPageViewModel CreateViewModel(
        ICalendarService calendarService,
        IPreferencesService preferencesService,
        DateOnly today)
    {
        var account = CreateAccount();

        var calendar = CreateCalendar(account, "Calendar");
        var accountCalendarViewModel = new AccountCalendarViewModel(account, calendar);
        var accountCalendarStateService = new FakeAccountCalendarStateService([accountCalendarViewModel]);

        return CreateViewModel(calendarService, preferencesService, today, accountCalendarStateService);
    }

    private static CalendarPageViewModel CreateViewModel(
        ICalendarService calendarService,
        IPreferencesService preferencesService,
        DateOnly today,
        IAccountCalendarStateService accountCalendarStateService)
    {
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

    private static AccountCalendar CreateCalendar(MailAccount account, string name)
        => new()
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            Name = name,
            RemoteCalendarId = "calendar",
            SynchronizationDeltaToken = string.Empty,
            TextColorHex = "#000000",
            BackgroundColorHex = "#ffffff",
            TimeZone = TimeZoneInfo.Utc.Id,
            IsExtended = true,
            IsPrimary = true,
            IsSynchronizationEnabled = true
        };

    private static MailAccount CreateAccount()
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "Primary",
            SenderName = "Primary",
            Address = "primary@example.com",
            ProviderType = MailProviderType.Outlook
        };

    private static CalendarItem CreateCalendarItem(Guid calendarId, DateTime startDate, string title)
        => new()
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            StartDate = startDate,
            DurationInSeconds = TimeSpan.FromMinutes(30).TotalSeconds,
            Title = title
        };

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
            true,
            workWeekStart,
            workWeekEnd,
            TimeSpan.FromHours(9),
            TimeSpan.FromHours(18),
            64,
            DayHeaderDisplayType.TwentyFourHour,
            CultureInfo.GetCultureInfo(cultureName));
    }

    private sealed class FakeAccountCalendarStateService : IAccountCalendarStateService
    {
        private readonly List<AccountCalendarViewModel> _calendars;
        private readonly List<AccountCalendarViewModel> _activeCalendars;
        private readonly ObservableCollection<GroupedAccountCalendarViewModel> _groupedCalendars = [];

        public FakeAccountCalendarStateService(IEnumerable<AccountCalendarViewModel> calendars, IEnumerable<AccountCalendarViewModel>? activeCalendars = null)
        {
            _calendars = calendars.ToList();
            _activeCalendars = (activeCalendars ?? _calendars.Where(calendar => calendar.IsChecked)).ToList();
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

        public IEnumerable<AccountCalendarViewModel> ActiveCalendars => _activeCalendars;
        public IEnumerable<AccountCalendarViewModel> AllCalendars => _calendars;
        public bool IsAnySynchronizationInProgress => false;
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
