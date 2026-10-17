using System.ComponentModel;
using System.Globalization;
using FluentAssertions;
using Moq;
using Wino.Calendar.ViewModels;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

/// <summary>
/// The calendar pages read what a calendar can do from <see cref="ICalendarCapabilityService"/>
/// and refuse denied actions themselves with a translated message.
/// </summary>
public sealed class CalendarCapabilityAdoptionTests
{
    private readonly Mock<ICalendarCapabilityService> _capabilities = new();
    private readonly Mock<IMailDialogService> _dialogs = new();
    private readonly Mock<IWinoRequestDelegator> _delegator = new();
    private readonly Mock<ICalendarService> _calendarService = new();

    private static readonly CalendarCapabilities ICloudLike = CalendarCapabilities.CalDavDefault with
    {
        IsServerReported = true,
        SupportsAttachments = true,
        SendsInvitations = true,
        MaxAttendees = 300
    };

    private static readonly CalendarCapabilities YahooLike = CalendarCapabilities.CalDavDefault with
    {
        IsServerReported = true
    };

    [Fact]
    public async Task Compose_LoadedCapabilities_DriveAttachmentsAttendeesAndSaveText()
    {
        var calendar = CalDavCalendar();
        _capabilities.Setup(service => service.GetCalendarCapabilitiesAsync(calendar.Id)).ReturnsAsync(ICloudLike);
        var viewModel = CreateComposeViewModel();
        var changed = TrackChanges(viewModel);

        viewModel.SelectedCalendar = calendar;
        await viewModel.CapabilityLoadTask;

        viewModel.CalendarCapabilities.Should().Be(ICloudLike);
        viewModel.CanAddAttachments.Should().BeTrue("the server supports managed attachments");
        viewModel.AttachmentsDisabledTooltipText.Should().BeEmpty();
        viewModel.CanAddAttendees.Should().BeTrue();
        viewModel.SendsInvitations.Should().BeTrue();
        viewModel.AttendeeLimitText.Should().Be(string.Format(Translator.CalendarCapability_AttendeeLimit, 300));
        changed.Should().Contain([nameof(viewModel.CanAddAttachments), nameof(viewModel.CanAddAttendees), nameof(viewModel.SaveButtonText), nameof(viewModel.AttendeeLimitText)]);

        viewModel.AddAttendee(new CalendarComposeAttendeeViewModel(string.Empty, "boss@other.test"));

        viewModel.SaveButtonText.Should().Be(Translator.Buttons_Send);
        viewModel.HasInvitationsNotSentWarning.Should().BeFalse();
    }

    [Fact]
    public async Task Compose_ServerWithoutScheduling_SavesInsteadOfSending_AndRefusesAttachments()
    {
        var calendar = CalDavCalendar();
        _capabilities.Setup(service => service.GetCalendarCapabilitiesAsync(calendar.Id)).ReturnsAsync(YahooLike);
        var viewModel = CreateComposeViewModel();

        viewModel.SelectedCalendar = calendar;
        await viewModel.CapabilityLoadTask;
        viewModel.AddAttendee(new CalendarComposeAttendeeViewModel(string.Empty, "boss@other.test"));

        viewModel.SaveButtonText.Should().Be(Translator.Buttons_Save);
        viewModel.InvitationsNotSentText.Should().Be(Translator.CalendarCapability_InvitationsNotSent);
        viewModel.CanAddAttachments.Should().BeFalse();
        viewModel.AttachmentsDisabledTooltipText.Should().Be(Translator.CalendarCapability_AttachmentsNotSupported);

        viewModel.TryAddAttachment("/tmp/agenda.pdf", 1024).Should().BeFalse();

        viewModel.Attachments.Should().BeEmpty();
        _dialogs.Verify(dialogs => dialogs.InfoBarMessage(
            Translator.CalendarCapability_ActionNotAvailableTitle,
            Translator.CalendarCapability_AttachmentsNotSupported,
            InfoBarMessageType.Warning), Times.Once);
    }

    [Fact]
    public async Task Compose_AttendeeBeyondTheServerLimit_IsRefused()
    {
        var calendar = CalDavCalendar();
        _capabilities.Setup(service => service.GetCalendarCapabilitiesAsync(calendar.Id)).ReturnsAsync(ICloudLike with { MaxAttendees = 1 });
        var viewModel = CreateComposeViewModel();
        viewModel.SelectedCalendar = calendar;
        await viewModel.CapabilityLoadTask;

        viewModel.AddAttendee(new CalendarComposeAttendeeViewModel(string.Empty, "first@other.test"));
        viewModel.IsAttendeeLimitReached.Should().BeTrue();
        viewModel.AddAttendee(new CalendarComposeAttendeeViewModel(string.Empty, "second@other.test"));

        viewModel.Attendees.Select(attendee => attendee.Email).Should().Equal("first@other.test");
        _dialogs.Verify(dialogs => dialogs.InfoBarMessage(
            Translator.CalendarCapability_ActionNotAvailableTitle,
            string.Format(Translator.CalendarCapability_AttendeeLimitReached, 1),
            InfoBarMessageType.Warning), Times.Once);
    }

    [Fact]
    public async Task Compose_BeforeTheServerAnswers_UsesTheConservativeCalDavDefaults()
    {
        var calendar = CalDavCalendar();
        var answer = new TaskCompletionSource<CalendarCapabilities>();
        _capabilities.Setup(service => service.GetCalendarCapabilitiesAsync(calendar.Id)).Returns(answer.Task);
        var viewModel = CreateComposeViewModel();

        viewModel.SelectedCalendar = calendar;

        viewModel.CalendarCapabilities.Should().Be(CalendarCapabilities.CalDavDefault);
        viewModel.CanAddAttachments.Should().BeFalse();
        viewModel.CanAddAttendees.Should().BeTrue();

        answer.SetResult(ICloudLike);
        await viewModel.CapabilityLoadTask;

        viewModel.CanAddAttachments.Should().BeTrue();
    }

    [Fact]
    public async Task Compose_LateAnswerForAnEarlierSelection_IsIgnored()
    {
        var first = CalDavCalendar();
        var second = CalDavCalendar();
        var firstAnswer = new TaskCompletionSource<CalendarCapabilities>();
        _capabilities.Setup(service => service.GetCalendarCapabilitiesAsync(first.Id)).Returns(firstAnswer.Task);
        _capabilities.Setup(service => service.GetCalendarCapabilitiesAsync(second.Id)).ReturnsAsync(YahooLike);
        var viewModel = CreateComposeViewModel();

        viewModel.SelectedCalendar = first;
        var firstLoad = viewModel.CapabilityLoadTask;
        viewModel.SelectedCalendar = second;
        await viewModel.CapabilityLoadTask;
        firstAnswer.SetResult(ICloudLike);
        await firstLoad;

        viewModel.CalendarCapabilities.Should().Be(YahooLike);
        viewModel.CanAddAttachments.Should().BeFalse();
    }

    [Fact]
    public async Task Compose_ReadOnlyCalendar_RefusesSaving()
    {
        var calendar = CalDavCalendar();
        _capabilities.Setup(service => service.GetCalendarCapabilitiesAsync(calendar.Id)).ReturnsAsync(ICloudLike with { IsReadOnly = true });
        var viewModel = CreateComposeViewModel();
        viewModel.SelectedCalendar = calendar;
        await viewModel.CapabilityLoadTask;

        viewModel.IsCalendarReadOnly.Should().BeTrue();
        viewModel.CanAddAttendees.Should().BeFalse();
        await viewModel.CreateCommand.ExecuteAsync(null);

        viewModel.LastCreationSucceeded.Should().BeFalse();
        _dialogs.Verify(dialogs => dialogs.ShowReadOnlyCalendarMessage(), Times.Once);
        _delegator.Verify(delegator => delegator.ExecuteAsync(It.IsAny<CalendarOperationPreparationRequest>()), Times.Never);
    }

    [Fact]
    public async Task Compose_LowQuota_ShowsAWarning()
    {
        var calendar = CalDavCalendar();
        _capabilities.Setup(service => service.GetCalendarCapabilitiesAsync(calendar.Id)).ReturnsAsync(ICloudLike with { QuotaAvailableBytes = 1024 * 1024 });
        var viewModel = CreateComposeViewModel();

        viewModel.SelectedCalendar = calendar;
        await viewModel.CapabilityLoadTask;

        viewModel.HasQuotaWarning.Should().BeTrue();
        viewModel.QuotaWarningText.Should().Be(string.Format(Translator.CalendarCapability_QuotaLow, CalendarCapabilityPresenter.FormatStorageSize(1024 * 1024)));
    }

    [Fact]
    public void Compose_WithoutTheService_OutlookKeepsTodaysBehavior()
    {
        var viewModel = CreateComposeViewModel(withCapabilityService: false);
        var account = new MailAccount { Id = Guid.NewGuid(), Address = "me@outlook.test", ProviderType = MailProviderType.Outlook, CalendarIntegrationSource = AccountIntegrationSource.Provider };

        viewModel.SelectedCalendar = new AccountCalendarViewModel(account, new AccountCalendar { Id = Guid.NewGuid(), AccountId = account.Id, Name = "Calendar" });
        viewModel.AddAttendee(new CalendarComposeAttendeeViewModel(string.Empty, "boss@other.test"));

        viewModel.CanAddAttachments.Should().BeTrue();
        viewModel.CanAddOnlineMeeting.Should().BeTrue();
        viewModel.SaveButtonText.Should().Be(Translator.Buttons_Send);
    }

    [Fact]
    public async Task Details_ReadOnlyCapability_RefusesTheResponse()
    {
        var calendarId = Guid.NewGuid();
        _capabilities.Setup(service => service.GetCalendarCapabilitiesAsync(calendarId)).ReturnsAsync(ICloudLike with { IsReadOnly = true });
        var viewModel = CreateDetailsViewModel();

        viewModel.CurrentEvent = CreateEvent(calendarId);
        await viewModel.CapabilityLoadTask;

        viewModel.IsCalendarReadOnly.Should().BeTrue();
        viewModel.CanRespondToInvitation.Should().BeFalse();
        await viewModel.SendRsvpResponseCommand.ExecuteAsync(AttendeeStatus.Accepted);

        _dialogs.Verify(dialogs => dialogs.ShowReadOnlyCalendarMessage(), Times.Once);
        _delegator.Verify(delegator => delegator.ExecuteAsync(It.IsAny<CalendarOperationPreparationRequest>()), Times.Never);
    }

    [Fact]
    public async Task Details_WritableCalendar_SendsTheResponse_AndWarnsWhenTheServerDoesNotDeliverIt()
    {
        var calendarId = Guid.NewGuid();
        _capabilities.Setup(service => service.GetCalendarCapabilitiesAsync(calendarId)).ReturnsAsync(YahooLike);
        _calendarService.Setup(service => service.GetAttendeesAsync(It.IsAny<Guid>())).ReturnsAsync(new List<CalendarEventAttendee>());
        var viewModel = CreateDetailsViewModel();

        viewModel.CurrentEvent = CreateEvent(calendarId);
        await viewModel.CapabilityLoadTask;
        await viewModel.SendRsvpResponseCommand.ExecuteAsync(AttendeeStatus.Accepted);

        viewModel.ResponseNotSentText.Should().Be(Translator.CalendarCapability_ResponseNotSent);
        _delegator.Verify(delegator => delegator.ExecuteAsync(It.Is<CalendarOperationPreparationRequest>(request => request.Operation == CalendarSynchronizerOperation.AcceptEvent)), Times.Once);
    }

    [Fact]
    public async Task AccountSettings_ExposesTheCapabilitySummary()
    {
        var account = CalDavAccount();
        var calendar = new AccountCalendar { Id = Guid.NewGuid(), AccountId = account.Id, Name = "Work", IsPrimary = true };
        var accounts = new Mock<IAccountService>();
        accounts.Setup(service => service.GetAccountAsync(account.Id)).ReturnsAsync(account);
        _calendarService.Setup(service => service.GetAccountCalendarAsync(calendar.Id)).ReturnsAsync(calendar);
        _capabilities.Setup(service => service.GetAccountCapabilitiesAsync(account.Id)).ReturnsAsync(ICloudLike with { QuotaAvailableBytes = 10L * 1024 * 1024 });
        var viewModel = new CalendarAccountSettingsPageViewModel(_calendarService.Object, accounts.Object, _capabilities.Object);

        await viewModel.InitializeNavigationAsync(Wino.Core.Domain.Models.Navigation.NavigationMode.New, calendar);

        viewModel.HasCapabilities.Should().BeTrue();
        viewModel.SupportsAttachments.Should().BeTrue();
        viewModel.SendsInvitations.Should().BeTrue();
        viewModel.MaxAttendees.Should().Be(300);
        viewModel.QuotaAvailableText.Should().Be("10 MB");
        viewModel.HasQuotaWarning.Should().BeFalse();
        viewModel.CapabilitySummaryText.Should().Be(string.Join(" ",
            Translator.CalendarCapability_SummaryAttachmentsSupported,
            Translator.CalendarCapability_SummaryInvitationsSent,
            string.Format(Translator.CalendarCapability_SummaryAttendeeLimit, 300),
            string.Format(Translator.CalendarCapability_SummaryQuota, "10 MB")));
    }

    [Fact]
    public void Summary_BeforeDiscovery_SaysFeaturesAreConfirmedLater()
    {
        var summary = CalendarCapabilityPresenter.BuildSummary(CalendarCapabilities.CalDavDefault);

        summary.Should().Contain(Translator.CalendarCapability_SummaryAttachmentsNotSupported);
        summary.Should().Contain(Translator.CalendarCapability_SummaryInvitationsNotSent);
        summary.Should().EndWith(Translator.CalendarCapability_SummaryPendingDiscovery);
    }

    private static List<string> TrackChanges(INotifyPropertyChanged source)
    {
        var names = new List<string>();
        source.PropertyChanged += (_, args) => names.Add(args.PropertyName ?? string.Empty);
        return names;
    }

    private static MailAccount CalDavAccount()
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "CalDAV",
            Address = "me@example.test",
            ProviderType = MailProviderType.IMAP4,
            CalendarIntegrationSource = AccountIntegrationSource.Dav,
            ServerInformation = new CustomServerInformation { CalendarSupportMode = ImapCalendarSupportMode.CalDav }
        };

    private static AccountCalendarViewModel CalDavCalendar()
    {
        var account = CalDavAccount();
        return new AccountCalendarViewModel(account, new AccountCalendar { Id = Guid.NewGuid(), AccountId = account.Id, Name = "Work" });
    }

    private static CalendarItemViewModel CreateEvent(Guid calendarId)
    {
        var calendar = new Mock<IAccountCalendar>();
        calendar.SetupProperty(value => value.IsReadOnly, false);
        calendar.SetupProperty(value => value.MailAccount, CalDavAccount());

        return new CalendarItemViewModel(new CalendarItem
        {
            Id = Guid.NewGuid(),
            CalendarId = calendarId,
            Title = "Planning",
            StartTimeZone = TimeZoneInfo.Local.Id,
            EndTimeZone = TimeZoneInfo.Local.Id,
            DurationInSeconds = 3600,
            OrganizerEmail = "boss@other.test",
            AssignedCalendar = calendar.Object
        });
    }

    private static Mock<IPreferencesService> CreatePreferences()
    {
        var preferences = new Mock<IPreferencesService>();
        preferences.Setup(service => service.GetCurrentCalendarSettings()).Returns(new CalendarSettings(
            DayOfWeek.Monday, [DayOfWeek.Monday], false, DayOfWeek.Monday, DayOfWeek.Friday,
            TimeSpan.FromHours(8), TimeSpan.FromHours(17), 52, DayHeaderDisplayType.TwentyFourHour, CultureInfo.InvariantCulture));
        preferences.SetupGet(service => service.DefaultReminderDurationInSeconds).Returns(15 * 60);
        return preferences;
    }

    private CalendarEventComposePageViewModel CreateComposeViewModel(bool withCapabilityService = true)
    {
        _calendarService.Setup(service => service.GetPredefinedReminderMinutes()).Returns([0, 5, 15, 30]);

        return new CalendarEventComposePageViewModel(
            Mock.Of<IAccountService>(),
            _calendarService.Object,
            Mock.Of<INavigationService>(),
            _dialogs.Object,
            Mock.Of<IContactService>(),
            CreatePreferences().Object,
            Mock.Of<IUnderlyingThemeService>(),
            _delegator.Object,
            calendarCapabilityService: withCapabilityService ? _capabilities.Object : null);
    }

    private EventDetailsPageViewModel CreateDetailsViewModel()
        => new(
            _calendarService.Object,
            Mock.Of<IExternalLauncher>(),
            CreatePreferences().Object,
            _dialogs.Object,
            _delegator.Object,
            Mock.Of<INavigationService>(),
            Mock.Of<INotificationBuilder>(),
            Mock.Of<IUnderlyingThemeService>(),
            Mock.Of<IContactService>(),
            Mock.Of<IApplicationConfiguration>(),
            calendarCapabilityService: _capabilities.Object);
}
