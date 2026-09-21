using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Exchange;

public class ExchangeAccountCapabilityTests
{
    private readonly Mock<ISynchronizationManager> _manager = new();
    private readonly Mock<IAccountService> _accounts = new();
    private readonly Mock<IContactService> _contacts = new();
    private readonly Mock<ITaskService> _tasks = new();

    private AccountCapabilityService CreateService(MailAccount account, MailAccount synchronizerAccount = null)
    {
        var synchronizer = new Mock<IWinoSynchronizerBase>();
        synchronizer.SetupGet(s => s.Account).Returns(synchronizerAccount ?? account);

        _manager.Setup(m => m.GetSynchronizerAsync(account.Id)).ReturnsAsync(synchronizer.Object);
        _manager.Setup(m => m.SynchronizeContactsAsync(It.IsAny<ContactSynchronizationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContactSynchronizationResult.Empty);
        _manager.Setup(m => m.SynchronizeTasksAsync(It.IsAny<TaskSynchronizationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaskSynchronizationResult.Empty);
        _accounts.Setup(a => a.GetAccountAsync(account.Id)).ReturnsAsync(account);
        _tasks.Setup(t => t.GetTaskListsAsync(account.Id)).ReturnsAsync(new List<AccountTaskList>());

        return new AccountCapabilityService(_manager.Object, _accounts.Object, _contacts.Object, _tasks.Object);
    }

    // The shape the wizard leaves behind for an account added for mail only.
    private static MailAccount MailOnlyExchangeAccount() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Exchange",
        ProviderType = MailProviderType.Exchange,
        IsMailAccessGranted = true,
        IsCalendarAccessEnabled = false,
        CalendarIntegrationSource = AccountIntegrationSource.Local,
        IsContactAccessEnabled = true,
        ContactIntegrationSource = AccountIntegrationSource.Local,
        IsTaskAccessEnabled = false,
        TaskIntegrationSource = AccountIntegrationSource.Dav
    };

    [Fact]
    public async Task EnablingCapabilities_MovesAnExchangeAccountToTheProviderSource()
    {
        var account = MailOnlyExchangeAccount();
        var synchronizerAccount = MailOnlyExchangeAccount();
        synchronizerAccount.Id = account.Id;
        var service = CreateService(account, synchronizerAccount);

        await service.ApplyAsync(account, includeMail: true, includeCalendar: true, includeContacts: true, includeTasks: true);

        foreach (var target in new[] { account, synchronizerAccount })
        {
            target.IsCalendarAccessEnabled.Should().BeTrue();
            target.IsCalendarAccessGranted.Should().BeTrue();
            target.CalendarIntegrationSource.Should().Be(AccountIntegrationSource.Provider);
            target.IsContactAccessGranted.Should().BeTrue();
            target.ContactIntegrationSource.Should().Be(AccountIntegrationSource.Provider);
            target.IsTaskAccessEnabled.Should().BeTrue();
            target.IsTaskAccessGranted.Should().BeTrue();
            target.TaskIntegrationSource.Should().Be(AccountIntegrationSource.Provider);
        }

        _manager.Verify(m => m.SynchronizeContactsAsync(It.Is<ContactSynchronizationOptions>(o => o.Type == ContactSynchronizationType.Full), It.IsAny<CancellationToken>()), Times.Once);
        _manager.Verify(m => m.SynchronizeTasksAsync(It.Is<TaskSynchronizationOptions>(o => o.Type == TaskSynchronizationType.Full), It.IsAny<CancellationToken>()), Times.Once);
        _manager.Verify(m => m.HandleAuthorizationAsync(It.IsAny<MailProviderType>(), It.IsAny<MailAccount>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<IReadOnlyCollection<ProviderFeature>>()), Times.Never);
        _accounts.Verify(a => a.UpdateAccountAsync(account), Times.Once);
    }

    [Fact]
    public async Task DisablingContactsAndTasks_ReturnsAnExchangeAccountToTheLocalBackends()
    {
        var account = MailOnlyExchangeAccount();
        account.IsContactAccessGranted = true;
        account.ContactIntegrationSource = AccountIntegrationSource.Provider;
        account.IsTaskAccessEnabled = true;
        account.IsTaskAccessGranted = true;
        account.TaskIntegrationSource = AccountIntegrationSource.Provider;
        var service = CreateService(account);

        await service.ApplyAsync(account, includeMail: true, includeCalendar: false, includeContacts: false, includeTasks: false);

        account.ContactIntegrationSource.Should().Be(AccountIntegrationSource.Local);
        account.TaskIntegrationSource.Should().Be(AccountIntegrationSource.Local);
        _contacts.Verify(c => c.DeleteAddressBooksBySourceAsync(account.Id, ContactSourceKind.Exchange), Times.Once);
        _contacts.Verify(c => c.EnsureLocalAddressBookAsync(account.Id, account.Name), Times.Once);
        _tasks.Verify(t => t.EnsureLocalTaskListAsync(account.Id, account.Name), Times.Once);
        _tasks.Verify(t => t.DeleteTaskListsBySourceAsync(account.Id, TaskSourceKind.Exchange), Times.Once);
    }

    [Fact]
    public async Task FailedTransition_RestoresTheSources()
    {
        var account = MailOnlyExchangeAccount();
        var service = CreateService(account);
        _manager.Setup(m => m.SynchronizeContactsAsync(It.IsAny<ContactSynchronizationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContactSynchronizationResult.Failed(new InvalidOperationException("offline")));

        var act = () => service.ApplyAsync(account, includeMail: true, includeCalendar: false, includeContacts: true, includeTasks: false);

        await act.Should().ThrowAsync<InvalidOperationException>();
        account.IsContactAccessGranted.Should().BeFalse();
        account.ContactIntegrationSource.Should().Be(AccountIntegrationSource.Local);
        account.TaskIntegrationSource.Should().Be(AccountIntegrationSource.Dav);
    }

    [Fact]
    public async Task OAuthAccounts_KeepTheirSources()
    {
        var account = MailOnlyExchangeAccount();
        account.ProviderType = MailProviderType.Outlook;
        account.ContactIntegrationSource = AccountIntegrationSource.Provider;
        var service = CreateService(account);

        await service.ApplyAsync(account, includeMail: true, includeCalendar: false, includeContacts: false, includeTasks: false);

        account.CalendarIntegrationSource.Should().Be(AccountIntegrationSource.Local);
        account.ContactIntegrationSource.Should().Be(AccountIntegrationSource.Provider);
        account.TaskIntegrationSource.Should().Be(AccountIntegrationSource.Dav);
    }
}
