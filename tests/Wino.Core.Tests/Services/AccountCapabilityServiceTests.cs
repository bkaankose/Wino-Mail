using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;
using Wino.Core.Services;
using Wino.Core.Tests.Helpers;
using Wino.Messaging.Client.Calendar;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

/// <summary>
/// Turning a connected feature off on the account details page must leave nothing behind that
/// the app could still show or act on. The service keeps the mode, backend and granted flags in
/// step and removes the data of a mode that is turned off.
/// </summary>
public class AccountCapabilityServiceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private CalendarService _calendarService = null!;
    private Mock<IAccountService> _accountService = null!;
    private Mock<ISynchronizationManager> _synchronizationManager = null!;
    private Mock<IContactService> _contactService = null!;
    private Mock<ITaskService> _taskService = null!;
    private AccountCapabilityService _service = null!;
    private MailAccount _account = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _calendarService = new CalendarService(_databaseService);

        _account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Work",
            Address = "work@example.test",
            ProviderType = MailProviderType.Outlook,
            IsMailAccessGranted = true,
            IsCalendarAccessGranted = true,
            IsCalendarAccessEnabled = true,
            CalendarIntegrationSource = AccountIntegrationSource.Provider,
            IsContactAccessGranted = true,
            IsContactAccessEnabled = true,
            ContactIntegrationSource = AccountIntegrationSource.Provider,
            IsTaskAccessGranted = true,
            IsTaskAccessEnabled = true,
            TaskIntegrationSource = AccountIntegrationSource.Provider
        };

        _accountService = new Mock<IAccountService>();
        _accountService.Setup(service => service.GetAccountAsync(_account.Id)).ReturnsAsync(() => _account);
        _synchronizationManager = new Mock<ISynchronizationManager>();
        _synchronizationManager
            .Setup(manager => manager.HandleAuthorizationAsync(
                It.IsAny<MailProviderType>(), It.IsAny<MailAccount>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IReadOnlyCollection<ProviderFeature>>()))
            .ReturnsAsync(new TokenInformationEx("access-token", "work@example.test", "work@example.test"));
        _contactService = new Mock<IContactService>();
        _taskService = new Mock<ITaskService>();
        _taskService.Setup(service => service.GetTaskListsAsync(It.IsAny<Guid?>())).ReturnsAsync([]);

        _service = new AccountCapabilityService(
            _synchronizationManager.Object,
            _accountService.Object,
            _contactService.Object,
            _taskService.Object,
            _calendarService);
    }

    public async Task DisposeAsync()
    {
        await _databaseService.DisposeAsync();
    }

    [Fact]
    public async Task DisablingCalendar_RemovesCalendarDataAndTurnsTheModeOff()
    {
        var calendar = await InsertCalendarWithEventAsync();
        var deletedCalendars = new List<AccountCalendar>();
        var recipient = new object();
        WeakReferenceMessenger.Default.Register<CalendarListDeleted>(recipient, (_, message) => deletedCalendars.Add(message.AccountCalendar));

        try
        {
            var result = await _service.ApplyAsync(_account, includeMail: true, includeCalendar: false, includeContacts: true, includeTasks: true);

            result.IsCalendarAccessGranted.Should().BeFalse();
            result.IsCalendarAccessEnabled.Should().BeFalse("nothing may treat the account as a local calendar afterwards");
            result.CalendarIntegrationSource.Should().Be(AccountIntegrationSource.Local);
            (await _databaseService.Connection.Table<AccountCalendar>().CountAsync()).Should().Be(0);
            (await _databaseService.Connection.Table<CalendarItem>().CountAsync()).Should().Be(0);
            deletedCalendars.Should().ContainSingle().Which.Id.Should().Be(calendar.Id);
            _synchronizationManager.Verify(manager => manager.CancelSynchronizationsAsync(_account.Id), Times.Once);
            _synchronizationManager.Verify(manager => manager.HandleAuthorizationAsync(
                It.IsAny<MailProviderType>(), It.IsAny<MailAccount>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IReadOnlyCollection<ProviderFeature>>()), Times.Never);
        }
        finally
        {
            WeakReferenceMessenger.Default.UnregisterAll(recipient);
        }
    }

    [Fact]
    public async Task DisablingCalendar_KeepsDataWhenTheAccountUpdateFails()
    {
        await InsertCalendarWithEventAsync();
        _accountService.Setup(service => service.UpdateAccountAsync(_account)).ThrowsAsync(new InvalidOperationException("database locked"));

        var act = () => _service.ApplyAsync(_account, includeMail: true, includeCalendar: false, includeContacts: true, includeTasks: true);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _account.IsCalendarAccessGranted.Should().BeTrue();
        _account.IsCalendarAccessEnabled.Should().BeTrue();
        _account.CalendarIntegrationSource.Should().Be(AccountIntegrationSource.Provider);
        (await _databaseService.Connection.Table<AccountCalendar>().CountAsync()).Should().Be(1);
        (await _databaseService.Connection.Table<CalendarItem>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task EnablingCalendar_UsesTheProviderBackendAfterAuthorization()
    {
        _account.IsCalendarAccessGranted = false;
        _account.IsCalendarAccessEnabled = false;
        _account.CalendarIntegrationSource = AccountIntegrationSource.Local;

        var result = await _service.ApplyAsync(_account, includeMail: true, includeCalendar: true, includeContacts: true, includeTasks: true);

        result.IsCalendarAccessGranted.Should().BeTrue();
        result.IsCalendarAccessEnabled.Should().BeTrue();
        result.CalendarIntegrationSource.Should().Be(AccountIntegrationSource.Provider);
        _synchronizationManager.Verify(manager => manager.HandleAuthorizationAsync(
            MailProviderType.Outlook, _account, false, true, It.IsAny<IReadOnlyCollection<ProviderFeature>>()), Times.Once);
    }

    [Fact]
    public async Task DisablingMail_RemovesDownloadedMailData()
    {
        var result = await _service.ApplyAsync(_account, includeMail: false, includeCalendar: true, includeContacts: true, includeTasks: true);

        result.IsMailAccessGranted.Should().BeFalse();
        _accountService.Verify(service => service.UpdateAccountAsync(_account), Times.Once);
        _accountService.Verify(service => service.DeleteAccountMailDataAsync(_account.Id), Times.Once);
        _synchronizationManager.Verify(manager => manager.CancelSynchronizationsAsync(_account.Id), Times.Once);
    }

    [Fact]
    public async Task DisablingContactsAndTasks_MovesBothModesToLocalStores()
    {
        var result = await _service.ApplyAsync(_account, includeMail: true, includeCalendar: true, includeContacts: false, includeTasks: false);

        result.IsContactAccessGranted.Should().BeFalse();
        result.IsContactAccessEnabled.Should().BeTrue();
        result.ContactIntegrationSource.Should().Be(AccountIntegrationSource.Local);
        result.IsTaskAccessGranted.Should().BeFalse();
        result.IsTaskAccessEnabled.Should().BeTrue();
        result.TaskIntegrationSource.Should().Be(AccountIntegrationSource.Local);
        _contactService.Verify(service => service.DeleteAddressBooksBySourceAsync(_account.Id, ContactSourceKind.Outlook), Times.Once);
        _contactService.Verify(service => service.EnsureLocalAddressBookAsync(_account.Id, _account.Name), Times.Once);
        _taskService.Verify(service => service.DeleteTaskListsBySourceAsync(_account.Id, TaskSourceKind.Outlook), Times.Once);
        _taskService.Verify(service => service.EnsureLocalTaskListAsync(_account.Id, _account.Name), Times.Once);
    }

    [Fact]
    public async Task UnchangedModes_KeepTheirFlagsAndData()
    {
        await InsertCalendarWithEventAsync();

        var result = await _service.ApplyAsync(_account, includeMail: true, includeCalendar: true, includeContacts: true, includeTasks: true);

        result.IsCalendarAccessEnabled.Should().BeTrue();
        result.CalendarIntegrationSource.Should().Be(AccountIntegrationSource.Provider);
        (await _databaseService.Connection.Table<AccountCalendar>().CountAsync()).Should().Be(1);
        _accountService.Verify(service => service.DeleteAccountMailDataAsync(It.IsAny<Guid>()), Times.Never);
        _synchronizationManager.Verify(manager => manager.CancelSynchronizationsAsync(It.IsAny<Guid>()), Times.Never);
    }

    private async Task<AccountCalendar> InsertCalendarWithEventAsync()
    {
        var calendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = _account.Id,
            Name = "Calendar",
            TimeZone = "UTC",
            IsPrimary = true
        };
        await _calendarService.InsertAccountCalendarAsync(calendar);

        await _calendarService.CreateNewCalendarItemAsync(new CalendarItem
        {
            Id = Guid.NewGuid(),
            Title = "Standup",
            StartDate = new DateTime(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc),
            DurationInSeconds = 900,
            CalendarId = calendar.Id
        }, null);

        return calendar;
    }
}
