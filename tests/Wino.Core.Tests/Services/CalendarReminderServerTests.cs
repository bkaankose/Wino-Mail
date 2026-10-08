using Moq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class CalendarReminderServerTests
{
    private static readonly TimeSpan ShortInterval = TimeSpan.FromMilliseconds(20);

    private readonly Mock<ICalendarService> _calendar = new();
    private readonly Mock<IAccountService> _accounts = new();
    private readonly Mock<INotificationBuilder> _notifications = new();

    [Fact]
    public async Task StartAsync_WithoutCalendarAccess_DoesNotStart()
    {
        SetupAccounts(calendarAccess: false);
        var server = CreateServer();

        await server.StartAsync();

        Assert.False(server.IsRunning);
        _calendar.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task StartAsync_WithCalendarAccess_RaisesDueReminders()
    {
        SetupAccounts(calendarAccess: true);
        var item = new CalendarItem { Id = Guid.NewGuid(), Title = "Standup" };
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _calendar.Setup(c => c.CheckAndNotifyAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<ISet<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CalendarReminderNotificationRequest>
            {
                new() { CalendarItem = item, ReminderDurationInSeconds = 300, ReminderKey = "standup" }
            });
        _notifications.Setup(n => n.CreateCalendarReminderNotificationAsync(item, 300))
            .Returns(Task.CompletedTask)
            .Callback(() => notified.TrySetResult());
        var server = CreateServer();

        await server.StartAsync();
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.StopAsync();

        Assert.False(server.IsRunning);
        _notifications.Verify(n => n.CreateCalendarReminderNotificationAsync(item, 300), Times.AtLeastOnce);
    }

    [Fact]
    public async Task StartAsync_Twice_StartsOnce()
    {
        SetupAccounts(calendarAccess: true);
        var server = CreateServer();

        await server.StartAsync();
        await server.StartAsync();
        await server.StopAsync();

        _accounts.Verify(a => a.GetAccountsAsync(), Times.Once);
    }

    [Fact]
    public async Task StopAsync_BeforeStart_IsNoOp()
    {
        var server = CreateServer();

        await server.StopAsync();

        Assert.False(server.IsRunning);
    }

    [Fact]
    public async Task StopAsync_EndsPolling_AndServerCanStartAgain()
    {
        SetupAccounts(calendarAccess: true);
        _calendar.Setup(c => c.CheckAndNotifyAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<ISet<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CalendarReminderNotificationRequest>());
        var server = CreateServer();

        await server.StartAsync();
        await server.StopAsync();
        var callsAfterStop = _calendar.Invocations.Count;
        await Task.Delay(ShortInterval * 5);

        Assert.Equal(callsAfterStop, _calendar.Invocations.Count);

        await server.StartAsync();
        Assert.True(server.IsRunning);
        await server.StopAsync();
        Assert.False(server.IsRunning);
    }

    private CalendarReminderServer CreateServer()
        => new(_calendar.Object, _accounts.Object, _notifications.Object, ShortInterval);

    private void SetupAccounts(bool calendarAccess)
        => _accounts.Setup(a => a.GetAccountsAsync())
            .ReturnsAsync(new List<MailAccount>
            {
                new() { Id = Guid.NewGuid(), Address = "me@example.test", IsCalendarAccessGranted = calendarAccess }
            });
}
