using System.Globalization;
using FluentAssertions;
using Moq;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class CalendarItemResizeTests
{
    private readonly Mock<ICalendarService> _calendarService = new();
    private readonly Mock<IWinoRequestDelegator> _delegator = new();
    private readonly Mock<IMailDialogService> _dialogs = new();

    [Fact]
    public async Task ResizeCalendarItemAsync_KeepsStartAndPersistsNewDuration()
    {
        var item = CreateItem(isReadOnly: false);
        var localStart = item.StartDate;
        CalendarOperationPreparationRequest? request = null;
        _delegator.Setup(delegator => delegator.ExecuteAsync(It.IsAny<CalendarOperationPreparationRequest>()))
            .Callback<CalendarOperationPreparationRequest>(value => request = value)
            .Returns(Task.CompletedTask);

        await CreateViewModel().ResizeCalendarItemAsync(item, localStart.AddMinutes(90));

        item.StartDate.Should().Be(localStart);
        item.DurationInSeconds.Should().Be(90 * 60);
        _calendarService.Verify(service => service.UpdateCalendarItemAsync(item.CalendarItem, It.IsAny<List<CalendarEventAttendee>>()), Times.Once);
        request.Should().NotBeNull();
        request!.Operation.Should().Be(CalendarSynchronizerOperation.ChangeStartAndEndDate);
        request.OriginalItem!.DurationInSeconds.Should().Be(60 * 60);
    }

    [Fact]
    public async Task ResizeCalendarItemAsync_IgnoresEndBeforeStart()
    {
        var item = CreateItem(isReadOnly: false);

        await CreateViewModel().ResizeCalendarItemAsync(item, item.StartDate.AddMinutes(-15));

        item.DurationInSeconds.Should().Be(60 * 60);
        _delegator.Verify(delegator => delegator.ExecuteAsync(It.IsAny<CalendarOperationPreparationRequest>()), Times.Never);
    }

    [Fact]
    public async Task ResizeCalendarItemAsync_RefusesReadOnlyCalendar()
    {
        var item = CreateItem(isReadOnly: true);

        await CreateViewModel().ResizeCalendarItemAsync(item, item.StartDate.AddHours(2));

        item.DurationInSeconds.Should().Be(60 * 60);
        _dialogs.Verify(dialogs => dialogs.ShowReadOnlyCalendarMessage(), Times.Once);
        _delegator.Verify(delegator => delegator.ExecuteAsync(It.IsAny<CalendarOperationPreparationRequest>()), Times.Never);
    }

    [Fact]
    public async Task MoveCalendarItemAsync_KeepsDuration()
    {
        var item = CreateItem(isReadOnly: false);
        var target = item.StartDate.AddDays(1).AddMinutes(30);

        await CreateViewModel().MoveCalendarItemAsync(item, target);

        item.StartDate.Should().Be(target);
        item.DurationInSeconds.Should().Be(60 * 60);
        _delegator.Verify(delegator => delegator.ExecuteAsync(It.IsAny<CalendarOperationPreparationRequest>()), Times.Once);
    }

    private CalendarItemViewModel CreateItem(bool isReadOnly)
    {
        var calendar = new Mock<IAccountCalendar>();
        calendar.SetupProperty(value => value.IsReadOnly, isReadOnly);
        var calendarItem = new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Design sync",
            StartTimeZone = TimeZoneInfo.Local.Id,
            EndTimeZone = TimeZoneInfo.Local.Id,
            DurationInSeconds = 60 * 60,
            AssignedCalendar = calendar.Object
        };
        var item = new CalendarItemViewModel(calendarItem);
        item.StartDate = new DateTime(2026, 10, 6, 10, 0, 0, DateTimeKind.Unspecified);
        return item;
    }

    private CalendarPageViewModel CreateViewModel()
    {
        _calendarService.Setup(service => service.GetAttendeesAsync(It.IsAny<Guid>())).ReturnsAsync(new List<CalendarEventAttendee>());
        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(service => service.GetCurrentCalendarSettings()).Returns(new CalendarSettings(
            DayOfWeek.Monday, [DayOfWeek.Monday], false, DayOfWeek.Monday, DayOfWeek.Friday,
            TimeSpan.FromHours(8), TimeSpan.FromHours(17), 52, DayHeaderDisplayType.TwentyFourHour, CultureInfo.InvariantCulture));

        return new CalendarPageViewModel(
            Mock.Of<IStatePersistanceService>(),
            _calendarService.Object,
            Mock.Of<INavigationService>(),
            Mock.Of<IExternalLauncher>(),
            Mock.Of<IAccountCalendarStateService>(),
            Mock.Of<INotificationBuilder>(),
            preferences.Object,
            _delegator.Object,
            _dialogs.Object,
            Mock.Of<IDateContextProvider>(),
            Mock.Of<ICalendarRangeTextFormatter>(),
            Mock.Of<ICalendarShellClient>());
    }
}
