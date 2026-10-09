using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.ViewModels.Companion;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests;

public sealed class CompanionActionHandlerTests
{
    public enum StoredTarget
    {
        Missing,
        WithoutAccount,
        DifferentAccount,
        MatchingAccount
    }

    [Theory]
    [InlineData(StoredTarget.Missing, false)]
    [InlineData(StoredTarget.Missing, true)]
    [InlineData(StoredTarget.WithoutAccount, false)]
    [InlineData(StoredTarget.WithoutAccount, true)]
    [InlineData(StoredTarget.DifferentAccount, true)]
    [InlineData(StoredTarget.MatchingAccount, false)]
    [InlineData(StoredTarget.MatchingAccount, true)]
    public async Task Actions_RequireAnExistingTargetWithTheOriginalAccount(StoredTarget target, bool originalHasAccount)
    {
        var accountId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var storedAccountId = target == StoredTarget.DifferentAccount ? Guid.NewGuid() : accountId;
        var storedHasAccount = target is StoredTarget.DifferentAccount or StoredTarget.MatchingAccount;
        var storedMail = target == StoredTarget.Missing ? null : new MailCopy
        {
            Id = itemId.ToString(),
            UniqueId = itemId,
            AssignedAccount = storedHasAccount ? new MailAccount { Id = storedAccountId } : null
        };
        var storedEvent = target == StoredTarget.Missing ? null : new CalendarItem
        {
            Id = itemId,
            AssignedCalendar = storedHasAccount ? new AccountCalendar { AccountId = storedAccountId } : null
        };
        var mail = new MailItemViewModel(new MailCopy
        {
            Id = itemId.ToString(),
            UniqueId = itemId,
            AssignedAccount = originalHasAccount ? new MailAccount { Id = accountId } : null
        });
        var calendarItem = new CalendarItemViewModel(new CalendarItem
        {
            Id = itemId,
            AssignedCalendar = originalHasAccount ? new AccountCalendar { AccountId = accountId } : null
        });

        var mails = new Mock<IMailService>();
        mails.Setup(service => service.GetSingleMailItemAsync(mail.Id)).ReturnsAsync(storedMail!);
        var calendars = new Mock<ICalendarService>();
        calendars.Setup(service => service.GetCalendarItemAsync(itemId)).ReturnsAsync(storedEvent!);
        var processor = new Mock<IWinoRequestProcessor>();
        processor.Setup(service => service.PrepareRequestsAsync(It.IsAny<MailOperationPreperationRequest>()))
            .ReturnsAsync(new List<IMailActionRequest>());
        using var services = new ServiceCollection()
            .AddSingleton(mails.Object)
            .AddSingleton(calendars.Object)
            .AddSingleton(processor.Object)
            .BuildServiceProvider();

        var navigationCount = 0;
        Task Navigate(Guid actualAccountId, Guid actualItemId, CancellationToken token)
        {
            actualAccountId.Should().Be(accountId);
            actualItemId.Should().Be(itemId);
            navigationCount++;
            return Task.CompletedTask;
        }

        var navigation = new CompanionNavigationCallbacks(
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            Navigate,
            Navigate,
            Navigate,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _, _) => Task.CompletedTask,
            _ => Task.CompletedTask);
        var handler = new CompanionActionHandler(services, navigation);

        await handler.OpenMailAsync(mail, CancellationToken.None);
        await handler.SetMailReadAsync(mail, true, CancellationToken.None);
        await handler.ArchiveMailAsync(mail, CancellationToken.None);
        await handler.OpenCalendarEventAsync(calendarItem, CancellationToken.None);
        await handler.JoinCalendarEventAsync(calendarItem, CancellationToken.None);

        var shouldAct = target == StoredTarget.MatchingAccount && originalHasAccount;
        navigationCount.Should().Be(shouldAct ? 3 : 0);
        processor.Verify(service => service.PrepareRequestsAsync(It.IsAny<MailOperationPreperationRequest>()),
            Times.Exactly(shouldAct ? 2 : 0));
    }
}
