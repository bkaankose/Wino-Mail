using System.Globalization;
using FluentAssertions;
using Moq;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Messaging.Client.Calendar;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

/// <summary>
/// The Mac details pane commits Show as and reminder changes without leaving the page
/// (<see cref="EventDetailsPageViewModel.NavigatesBackAfterSave"/>), and its pop-out window never
/// navigates the shell (<see cref="EventDetailsPageViewModel.CanNavigateShell"/>). Windows keeps the defaults.
/// </summary>
public sealed class EventDetailsSaveTests
{
    private readonly Mock<ICalendarService> _calendarService = new();
    private readonly Mock<IWinoRequestDelegator> _delegator = new();
    private readonly Mock<IMailDialogService> _dialogs = new();
    private readonly Mock<INavigationService> _navigation = new();

    [Fact]
    public async Task Save_WithDefaults_NavigatesBackToCalendar()
    {
        var viewModel = CreateViewModel(CreateItem(isReadOnly: false));

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.LastMutationSucceeded.Should().BeTrue();
        VerifyCalendarNavigation(Times.Once());
    }

    [Fact]
    public async Task Save_WithoutNavigation_PersistsShowAsAndRemindersAndStays()
    {
        var item = CreateItem(isReadOnly: false);
        var viewModel = CreateViewModel(item);
        viewModel.NavigatesBackAfterSave = false;
        viewModel.SelectedShowAsOption = viewModel.ShowAsOptions.First(option => option.ShowAs == CalendarItemShowAs.Free);
        viewModel.ReminderOptions.Add(new ReminderOption(15) { IsSelected = true });
        List<Reminder>? savedReminders = null;
        _calendarService.Setup(service => service.SaveRemindersAsync(item.Id, It.IsAny<List<Reminder>>()))
            .Callback<Guid, List<Reminder>>((_, reminders) => savedReminders = reminders)
            .Returns(Task.CompletedTask);
        CalendarOperationPreparationRequest? request = null;
        _delegator.Setup(delegator => delegator.ExecuteAsync(It.IsAny<CalendarOperationPreparationRequest>()))
            .Callback<CalendarOperationPreparationRequest>(value => request = value)
            .Returns(Task.CompletedTask);

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.LastMutationSucceeded.Should().BeTrue();
        item.CalendarItem.ShowAs.Should().Be(CalendarItemShowAs.Free);
        _calendarService.Verify(service => service.UpdateCalendarItemAsync(item.CalendarItem, It.IsAny<List<CalendarEventAttendee>>()), Times.Once);
        savedReminders.Should().ContainSingle().Which.DurationInSeconds.Should().Be(15 * 60);
        request.Should().NotBeNull();
        request!.Operation.Should().Be(CalendarSynchronizerOperation.UpdateEvent);
        VerifyCalendarNavigation(Times.Never());
    }

    [Fact]
    public async Task Save_WithoutNavigation_RefreshesCurrentEventFromTheUpdateMessage()
    {
        var item = CreateItem(isReadOnly: false);
        var viewModel = CreateViewModel(item);
        viewModel.NavigatesBackAfterSave = false;
        viewModel.SelectedShowAsOption = viewModel.ShowAsOptions.First(option => option.ShowAs == CalendarItemShowAs.Tentative);

        await viewModel.SaveCommand.ExecuteAsync(null);
        // The calendar service announces the optimistic change; the page stays and shows it.
        viewModel.Receive(new CalendarItemUpdated(item.CalendarItem, EntityUpdateSource.ClientUpdated));

        viewModel.CurrentEvent.Should().NotBeSameAs(item);
        viewModel.CurrentEvent.CalendarItem.ShowAs.Should().Be(CalendarItemShowAs.Tentative);
        viewModel.CurrentEvent.IsBusy.Should().BeTrue();
        VerifyCalendarNavigation(Times.Never());
    }

    [Fact]
    public async Task Delete_WhenShellNavigationIsOff_DoesNotNavigate()
    {
        var viewModel = CreateViewModel(CreateItem(isReadOnly: false));
        viewModel.CanNavigateShell = false;

        await viewModel.DeleteCommand.ExecuteAsync(null);

        viewModel.LastMutationSucceeded.Should().BeTrue();
        _delegator.Verify(delegator => delegator.ExecuteAsync(It.Is<CalendarOperationPreparationRequest>(value => value.Operation == CalendarSynchronizerOperation.DeleteEvent)), Times.Once);
        VerifyCalendarNavigation(Times.Never());
    }

    [Fact]
    public async Task Save_ReadOnlyCalendar_ShowsMessageAndStays()
    {
        var viewModel = CreateViewModel(CreateItem(isReadOnly: true));
        viewModel.NavigatesBackAfterSave = false;

        await viewModel.SaveCommand.ExecuteAsync(null);

        viewModel.LastMutationSucceeded.Should().BeFalse();
        _dialogs.Verify(dialogs => dialogs.ShowReadOnlyCalendarMessage(), Times.Once);
        _calendarService.Verify(service => service.UpdateCalendarItemAsync(It.IsAny<CalendarItem>(), It.IsAny<List<CalendarEventAttendee>>()), Times.Never);
        VerifyCalendarNavigation(Times.Never());
    }

    private void VerifyCalendarNavigation(Times times)
        => _navigation.Verify(navigation => navigation.Navigate(WinoPage.CalendarPage, It.IsAny<object?>(), It.IsAny<NavigationReferenceFrame?>(), It.IsAny<NavigationTransitionType>()), times);

    private static CalendarItemViewModel CreateItem(bool isReadOnly)
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
            ShowAs = CalendarItemShowAs.Busy,
            AssignedCalendar = calendar.Object
        };
        return new CalendarItemViewModel(calendarItem);
    }

    private EventDetailsPageViewModel CreateViewModel(CalendarItemViewModel item)
    {
        _calendarService.Setup(service => service.GetCalendarItemAsync(item.Id)).ReturnsAsync(item.CalendarItem);
        _calendarService.Setup(service => service.GetAttendeesAsync(It.IsAny<Guid>())).ReturnsAsync(new List<CalendarEventAttendee>());
        _calendarService.Setup(service => service.GetRemindersAsync(It.IsAny<Guid>())).ReturnsAsync(new List<Reminder>());
        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(service => service.GetCurrentCalendarSettings()).Returns(new CalendarSettings(
            DayOfWeek.Monday, [DayOfWeek.Monday], false, DayOfWeek.Monday, DayOfWeek.Friday,
            TimeSpan.FromHours(8), TimeSpan.FromHours(17), 52, DayHeaderDisplayType.TwentyFourHour, CultureInfo.InvariantCulture));

        var viewModel = new EventDetailsPageViewModel(
            _calendarService.Object,
            Mock.Of<IExternalLauncher>(),
            preferences.Object,
            _dialogs.Object,
            _delegator.Object,
            _navigation.Object,
            Mock.Of<INotificationBuilder>(),
            Mock.Of<IUnderlyingThemeService>(),
            Mock.Of<IContactService>(),
            Mock.Of<IApplicationConfiguration>());
        viewModel.CurrentEvent = item;
        return viewModel;
    }
}
