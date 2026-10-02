using System.Reflection;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Synchronization;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public class ContactsPageSynchronizationTests
{
    [Theory]
    [InlineData(SynchronizationCompletedState.Failed, SynchronizationCompletedState.Success)]
    [InlineData(SynchronizationCompletedState.PartiallyCompleted, SynchronizationCompletedState.Canceled)]
    public async Task Refresh_ReportsEachFailedAccountSeparately_AndSkipsSuccessfulOrCanceledAccounts(
        SynchronizationCompletedState failureState, SynchronizationCompletedState otherState)
    {
        var accounts = new[]
        {
            new MailAccount { Id = Guid.NewGuid(), Name = "Work", Address = "one@example.com", IsContactAccessGranted = true },
            new MailAccount { Id = Guid.NewGuid(), Name = "Work", Address = "two@example.com", IsContactAccessGranted = true },
            new MailAccount { Id = Guid.NewGuid(), Name = "Personal", Address = "ok@example.com", IsContactAccessGranted = true }
        };
        var sync = new Mock<ISynchronizationManager>();
        sync.Setup(s => s.SynchronizeContactsAsync(It.IsAny<ContactSynchronizationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ContactSynchronizationOptions options, CancellationToken _) => options.AccountId == accounts[2].Id
                ? new ContactSynchronizationResult { CompletedState = otherState }
                : new ContactSynchronizationResult { CompletedState = failureState });
        var queries = new Mock<IContactQueryService>();
        queries.Setup(q => q.GetContactsPageAsync(It.IsAny<ContactQueryFilter>(), 0, 50, It.IsAny<ContactSortOrder>()))
            .ReturnsAsync(new PagedContactsResult(Array.Empty<AccountContact>(), 0, false, 0, 50));
        var messages = new List<string>();
        var dialogs = new Mock<IMailDialogService>();
        dialogs.Setup(d => d.InfoBarMessage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<InfoBarMessageType>()))
            .Callback<string, string, InfoBarMessageType>((title, message, _) => messages.Add(title + " " + message));
        var vm = new ContactsPageViewModel(queries.Object, Mock.Of<IAccountService>(), sync.Object,
            Mock.Of<IWinoRequestDelegator>(), Mock.Of<INavigationService>(), dialogs.Object, Mock.Of<IActivationStateService>());
        typeof(ContactsPageViewModel).GetField("_accounts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, accounts.ToDictionary(a => a.Id));

        await vm.RefreshContactsCommand.ExecuteAsync(null);

        messages.Should().HaveCount(2);
        messages[0].Should().Contain("Work").And.Contain("one@example.com");
        messages[1].Should().Contain("Work").And.Contain("two@example.com");
        messages.Should().NotContain(message => message.Contains("ok@example.com"));
        vm.IsRefreshing.Should().BeFalse();
    }
}
