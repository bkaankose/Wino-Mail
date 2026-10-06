using FluentAssertions;
using Moq;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Interfaces;
using Xunit;

namespace Wino.Core.Tests;

public sealed class CalendarAccountSettingsPersistenceTests
{
    [Fact]
    public async Task AcceptedEditsWaitForEarlierWriteBeforePersistingLatestState()
    {
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new Mock<ICalendarService>();
        var persisted = new List<bool>();
        service.Setup(value => value.UpdateAccountCalendarAsync(It.IsAny<AccountCalendar>()))
            .Returns((AccountCalendar calendar) =>
            {
                persisted.Add(calendar.IsSynchronizationEnabled);
                return persisted.Count == 1 ? firstWrite.Task : Task.CompletedTask;
            });
        var viewModel = new CalendarAccountSettingsPageViewModel(service.Object, Mock.Of<IAccountService>())
        { AccountCalendar = new AccountCalendar() };

        viewModel.IsSyncEnabled = true;
        viewModel.IsSyncEnabled = false;

        persisted.Should().Equal(true);
        viewModel.PendingPersistence.IsCompleted.Should().BeFalse();
        firstWrite.SetResult();
        await viewModel.PendingPersistence;
        persisted.Should().Equal(true, false);
    }

    [Fact]
    public async Task FailedWriteRemainsObservableUntilExplicitRetrySucceeds()
    {
        var service = new Mock<ICalendarService>();
        service.SetupSequence(value => value.UpdateAccountCalendarAsync(It.IsAny<AccountCalendar>()))
            .Returns(Task.FromException(new IOException("Calendar preferences could not be stored.")))
            .Returns(Task.CompletedTask);
        var viewModel = new CalendarAccountSettingsPageViewModel(service.Object, Mock.Of<IAccountService>())
        { AccountCalendar = new AccountCalendar() };

        viewModel.IsSyncEnabled = true;
        await Assert.ThrowsAsync<IOException>(() => viewModel.PendingPersistence);
        viewModel.PendingPersistence.IsFaulted.Should().BeTrue();

        await viewModel.RetryPersistenceAsync();
        viewModel.PendingPersistence.IsCompletedSuccessfully.Should().BeTrue();
        viewModel.AccountCalendar.IsSynchronizationEnabled.Should().BeTrue();
        service.Verify(value => value.UpdateAccountCalendarAsync(viewModel.AccountCalendar), Times.Exactly(2));
    }
}
