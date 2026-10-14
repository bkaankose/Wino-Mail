using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;
using Wino.Messaging.Server;
using Wino.Messaging.UI;
using Xunit;

namespace Wino.Core.Tests;

public sealed class ApplicationRuntimeTests
{
    [Fact]
    public async Task ConcurrentInitialization_InstallsOnlyOneRecipientSet()
    {
        var entered = Signal();
        var release = Signal();
        var calls = 0;
        var messenger = new WeakReferenceMessenger();
        var runtime = CreateRuntime(async _ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task;
        }, messenger: messenger);

        var starts = Enumerable.Range(0, 8).Select(_ => runtime.InitializeAsync()).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        calls.Should().Be(1);
        release.SetResult();
        await Task.WhenAll(starts);

        runtime.State.Should().Be(ApplicationRuntimeState.Initialized);
        messenger.IsRegistered<NewMailSynchronizationRequested>(runtime).Should().BeTrue();
        await runtime.StopAsync();
        messenger.IsRegistered<NewMailSynchronizationRequested>(runtime).Should().BeFalse();
    }

    [Fact]
    public async Task SafeInitializationFailure_RollsBackAndAllowsRetry()
    {
        var calls = 0;
        var messenger = new WeakReferenceMessenger();
        var runtime = CreateRuntime(_ => Interlocked.Increment(ref calls) == 1
            ? Task.FromException(new IOException("Unavailable startup input.")) : Task.CompletedTask,
            messenger: messenger);

        await runtime.Invoking(x => x.InitializeAsync()).Should().ThrowAsync<IOException>();
        runtime.State.Should().Be(ApplicationRuntimeState.Uninitialized);
        messenger.IsRegistered<NewMailSynchronizationRequested>(runtime).Should().BeFalse();

        await runtime.InitializeAsync();
        calls.Should().Be(2);
        messenger.IsRegistered<NewMailSynchronizationRequested>(runtime).Should().BeTrue();
        await runtime.StopAsync();
    }

    [Fact]
    public async Task PartialSubscriptionFailure_IsTerminalAndNeverRetried()
    {
        var calls = 0;
        var runtime = CreateRuntime(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromException(new RuntimeInitializationFaultException(new InvalidOperationException("Partial subscription.")));
        });

        await runtime.Invoking(x => x.InitializeAsync()).Should().ThrowAsync<RuntimeInitializationFaultException>();
        runtime.State.Should().Be(ApplicationRuntimeState.Faulted);
        await runtime.Invoking(x => x.InitializeAsync()).Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(1);
        await runtime.StopAsync();
    }

    [Fact]
    public async Task CallerCancellation_DoesNotCancelSharedInitialization()
    {
        var release = Signal();
        var runtime = CreateRuntime(_ => release.Task);
        using var caller = new CancellationTokenSource();
        var canceledWait = runtime.InitializeAsync(caller.Token);
        var survivingWait = runtime.InitializeAsync();

        caller.Cancel();
        await FluentActions.Awaiting(() => canceledWait).Should().ThrowAsync<OperationCanceledException>();
        release.SetResult();
        await survivingWait;

        runtime.State.Should().Be(ApplicationRuntimeState.Initialized);
        await runtime.StopAsync();
    }

    [Fact]
    public async Task StateSubscriber_CanStopSynchronouslyWithoutDeadlockOrRestart()
    {
        var stoppedFromCallback = Signal();
        var runtime = CreateRuntime(_ => Task.CompletedTask);
        runtime.StateChanged += (_, state) =>
        {
            if (state != ApplicationRuntimeState.Initialized) return;
            runtime.StopAsync().GetAwaiter().GetResult();
            stoppedFromCallback.TrySetResult();
        };

        await runtime.InitializeAsync();
        await stoppedFromCallback.Task.WaitAsync(TimeSpan.FromSeconds(5));

        runtime.State.Should().Be(ApplicationRuntimeState.Stopped);
        await runtime.Invoking(x => x.StartAsync()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RepeatedStart_RescheduleAndLastAccountRemoval_PreserveTimerOwnership()
    {
        var clock = new ManualTimeProvider();
        // This fixture tests scheduling ownership, without invoking provider synchronization.
        // Mail access defaults to true, which would require a configured synchronization result.
        var account = new MailAccount { Id = Guid.NewGuid(), IsMailAccessGranted = false };
        var accounts = new Mock<IAccountService>();
        accounts.Setup(x => x.GetAccountsAsync()).ReturnsAsync(new List<MailAccount> { account });
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupGet(x => x.EmailSyncIntervalMinutes).Returns(2);
        preferences.SetupGet(x => x.CalendarSyncIntervalMinutes).Returns(3);
        var messenger = new WeakReferenceMessenger();
        var runtime = CreateRuntime(_ => Task.CompletedTask, clock, accounts, preferences, messenger);

        await Task.WhenAll(runtime.StartAsync(), runtime.StartAsync());
        // Mail and calendar loops, plus the task and contact due checks.
        await clock.WaitForPeriodicTimersAsync(4);
        clock.PeriodicTimersCreated.Should().Be(4);

        preferences.SetupGet(x => x.EmailSyncIntervalMinutes).Returns(4);
        preferences.Raise(x => x.PreferenceChanged += null, preferences.Object, nameof(IPreferencesService.EmailSyncIntervalMinutes));
        await clock.WaitForPeriodicTimersAsync(5);
        await clock.WaitForActivePeriodsAsync(TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        clock.ActivePeriods.Should().BeEquivalentTo(new[] { TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15) });

        var removalRead = Signal();
        accounts.Setup(x => x.GetAccountsAsync()).Returns(() =>
        {
            removalRead.TrySetResult();
            return Task.FromResult(new List<MailAccount>());
        });
        messenger.Send(new AccountRemovedMessage(account));
        await removalRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clock.WaitForActivePeriodsAsync();
        await runtime.StopAsync();
        clock.ActivePeriods.Should().BeEmpty();
        runtime.State.Should().Be(ApplicationRuntimeState.Stopped);
        await runtime.StopAsync();
        await runtime.Invoking(x => x.StartAsync()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CanceledStopWait_DoesNotReportStoppedBeforeInitializationDrains()
    {
        var entered = Signal();
        var release = Signal();
        var runtime = CreateRuntime(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        var initialization = runtime.InitializeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var caller = new CancellationTokenSource();
        var stoppedWait = runtime.StopAsync(caller.Token);
        caller.Cancel();

        await FluentActions.Awaiting(() => stoppedWait).Should().ThrowAsync<OperationCanceledException>();
        runtime.State.Should().Be(ApplicationRuntimeState.Stopping);

        release.SetResult();
        await FluentActions.Awaiting(() => initialization).Should().ThrowAsync<OperationCanceledException>();
        await runtime.StopAsync();
        runtime.State.Should().Be(ApplicationRuntimeState.Stopped);
    }

    [Fact]
    public async Task RareModes_SynchronizeOnlyWhenPersistedTimestampIsSixHoursOld()
    {
        var clock = new ManualTimeProvider();
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            IsMailAccessGranted = false,
            IsTaskAccessGranted = true,
            IsContactAccessGranted = true
        };
        var accounts = new Mock<IAccountService>();
        accounts.Setup(x => x.GetAccountsAsync()).ReturnsAsync(new List<MailAccount> { account });
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupGet(x => x.EmailSyncIntervalMinutes).Returns(60);
        preferences.SetupGet(x => x.CalendarSyncIntervalMinutes).Returns(60);
        preferences.SetupProperty(x => x.TaskLastAutoSynchronizationUtcTicks, 0L);
        // Contacts last ran five hours before the clock starts, so they are due after one more hour.
        var start = DateTimeOffset.UnixEpoch.AddHours(10);
        clock.Advance(start - clock.GetUtcNow());
        preferences.SetupProperty(x => x.ContactLastAutoSynchronizationUtcTicks, start.AddHours(-5).UtcTicks);

        var taskSyncs = 0;
        var contactSyncs = 0;
        var manager = new Mock<ISynchronizationManager>();
        manager.Setup(x => x.GetAllSynchronizers()).Returns(Array.Empty<IWinoSynchronizerBase>());
        manager.Setup(x => x.SynchronizeTasksAsync(It.IsAny<TaskSynchronizationOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref taskSyncs))
            .ReturnsAsync(TaskSynchronizationResult.Empty);
        manager.Setup(x => x.SynchronizeContactsAsync(It.IsAny<ContactSynchronizationOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref contactSyncs))
            .ReturnsAsync(ContactSynchronizationResult.Empty);
        var runtime = CreateRuntime(_ => Task.CompletedTask, clock, accounts, preferences, manager: manager);

        await runtime.StartAsync();
        await clock.WaitForPeriodicTimersAsync(4);

        // Nothing runs at launch, and the first check only starts the task window.
        taskSyncs.Should().Be(0);
        contactSyncs.Should().Be(0);
        await AdvanceAndSettleAsync(clock, TimeSpan.FromMinutes(15));
        preferences.Object.TaskLastAutoSynchronizationUtcTicks.Should().Be(start.AddMinutes(15).UtcTicks);
        taskSyncs.Should().Be(0);
        contactSyncs.Should().Be(0);

        for (var i = 0; i < 4; i++)
            await AdvanceAndSettleAsync(clock, TimeSpan.FromMinutes(15));
        contactSyncs.Should().Be(1);
        taskSyncs.Should().Be(0);

        for (var i = 0; i < 20; i++)
            await AdvanceAndSettleAsync(clock, TimeSpan.FromMinutes(15));
        taskSyncs.Should().Be(1);
        contactSyncs.Should().Be(1);

        await runtime.StopAsync();
    }

    [Fact]
    public async Task CreatedAccount_SynchronizesTasksAndContactsWithoutTouchingTheWindow()
    {
        var clock = new ManualTimeProvider();
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            IsMailAccessGranted = false,
            IsTaskAccessGranted = true,
            IsContactAccessGranted = true
        };
        var accounts = new Mock<IAccountService>();
        accounts.Setup(x => x.GetAccountsAsync()).ReturnsAsync(new List<MailAccount> { account });
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupGet(x => x.EmailSyncIntervalMinutes).Returns(60);
        preferences.SetupGet(x => x.CalendarSyncIntervalMinutes).Returns(60);
        // A window that started a minute ago is far from due.
        var recent = clock.GetUtcNow().AddMinutes(-1).UtcTicks;
        preferences.SetupProperty(x => x.TaskLastAutoSynchronizationUtcTicks, recent);
        preferences.SetupProperty(x => x.ContactLastAutoSynchronizationUtcTicks, recent);
        var manager = new Mock<ISynchronizationManager>();
        manager.Setup(x => x.GetAllSynchronizers()).Returns(Array.Empty<IWinoSynchronizerBase>());
        manager.Setup(x => x.SynchronizeTasksAsync(It.IsAny<TaskSynchronizationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaskSynchronizationResult.Empty);
        manager.Setup(x => x.SynchronizeContactsAsync(It.IsAny<ContactSynchronizationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContactSynchronizationResult.Empty);
        var runtime = CreateRuntime(_ => Task.CompletedTask, clock, accounts, preferences, manager: manager);
        await runtime.StartAsync();

        await runtime.SynchronizeCreatedAccountAsync(account);

        manager.Verify(x => x.SynchronizeTasksAsync(It.Is<TaskSynchronizationOptions>(o => o.AccountId == account.Id), It.IsAny<CancellationToken>()), Times.Once);
        manager.Verify(x => x.SynchronizeContactsAsync(It.Is<ContactSynchronizationOptions>(o => o.AccountId == account.Id), It.IsAny<CancellationToken>()), Times.Once);
        preferences.Object.TaskLastAutoSynchronizationUtcTicks.Should().Be(recent);
        preferences.Object.ContactLastAutoSynchronizationUtcTicks.Should().Be(recent);
        await runtime.StopAsync();
    }

    [Fact]
    public async Task RemovingLastAccount_ResetsRareModeSynchronizationDates()
    {
        var account = new MailAccount { Id = Guid.NewGuid(), IsMailAccessGranted = false };
        var accounts = new Mock<IAccountService>();
        accounts.Setup(x => x.GetAccountsAsync()).ReturnsAsync(new List<MailAccount> { account });
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupGet(x => x.EmailSyncIntervalMinutes).Returns(60);
        preferences.SetupGet(x => x.CalendarSyncIntervalMinutes).Returns(60);
        preferences.SetupProperty(x => x.TaskLastAutoSynchronizationUtcTicks, 1234L);
        preferences.SetupProperty(x => x.ContactLastAutoSynchronizationUtcTicks, 5678L);
        var messenger = new WeakReferenceMessenger();
        var runtime = CreateRuntime(_ => Task.CompletedTask, new ManualTimeProvider(), accounts, preferences, messenger);
        await runtime.StartAsync();
        preferences.Object.TaskLastAutoSynchronizationUtcTicks.Should().Be(1234L);

        var contactsReset = Signal();
        preferences.SetupSet(x => x.ContactLastAutoSynchronizationUtcTicks = 0L).Callback(() => contactsReset.TrySetResult());
        accounts.Setup(x => x.GetAccountsAsync()).ReturnsAsync(new List<MailAccount>());
        messenger.Send(new AccountRemovedMessage(account));
        await contactsReset.Task.WaitAsync(TimeSpan.FromSeconds(5));

        preferences.Object.TaskLastAutoSynchronizationUtcTicks.Should().Be(0L);
        await runtime.StopAsync();
    }

    [Fact]
    public async Task SynchronizeOnce_RunsOneBoundedPassWithoutStartingLoops()
    {
        var clock = new ManualTimeProvider();
        var start = DateTimeOffset.UnixEpoch.AddHours(10);
        clock.Advance(start - clock.GetUtcNow());
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            IsMailAccessGranted = true,
            IsCalendarAccessGranted = true,
            IsTaskAccessGranted = true,
            IsContactAccessGranted = true
        };
        var accounts = new Mock<IAccountService>();
        accounts.Setup(x => x.GetAccountsAsync()).ReturnsAsync(new List<MailAccount> { account });
        accounts.Setup(x => x.GetAccountAsync(account.Id)).ReturnsAsync(account);
        var preferences = new Mock<IPreferencesService>();
        preferences.SetupGet(x => x.EmailSyncIntervalMinutes).Returns(60);
        preferences.SetupGet(x => x.CalendarSyncIntervalMinutes).Returns(60);
        // Tasks have no window yet, so the pass only starts it; contacts are seven hours old and due.
        preferences.SetupProperty(x => x.TaskLastAutoSynchronizationUtcTicks, 0L);
        preferences.SetupProperty(x => x.ContactLastAutoSynchronizationUtcTicks, start.AddHours(-7).UtcTicks);
        var manager = new Mock<ISynchronizationManager>();
        manager.Setup(x => x.GetAllSynchronizers()).Returns(Array.Empty<IWinoSynchronizerBase>());
        manager.Setup(x => x.SynchronizeMailAsync(It.IsAny<MailSynchronizationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MailSynchronizationResult.Empty);
        manager.Setup(x => x.SynchronizeCalendarAsync(It.IsAny<CalendarSynchronizationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CalendarSynchronizationResult.Empty);
        manager.Setup(x => x.SynchronizeTasksAsync(It.IsAny<TaskSynchronizationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaskSynchronizationResult.Empty);
        manager.Setup(x => x.SynchronizeContactsAsync(It.IsAny<ContactSynchronizationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContactSynchronizationResult.Empty);
        var runtime = CreateRuntime(_ => Task.CompletedTask, clock, accounts, preferences, manager: manager);

        await runtime.SynchronizeOnceAsync();

        manager.Verify(x => x.SynchronizeMailAsync(It.Is<MailSynchronizationOptions>(o => o.AccountId == account.Id && o.Type == MailSynchronizationType.InboxOnly), It.IsAny<CancellationToken>()), Times.Once);
        manager.Verify(x => x.SynchronizeCalendarAsync(It.Is<CalendarSynchronizationOptions>(o => o.AccountId == account.Id && o.Type == CalendarSynchronizationType.CalendarMetadata), It.IsAny<CancellationToken>()), Times.Once);
        manager.Verify(x => x.SynchronizeContactsAsync(It.Is<ContactSynchronizationOptions>(o => o.AccountId == account.Id), It.IsAny<CancellationToken>()), Times.Once);
        manager.Verify(x => x.SynchronizeTasksAsync(It.IsAny<TaskSynchronizationOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        preferences.Object.TaskLastAutoSynchronizationUtcTicks.Should().Be(start.UtcTicks);
        preferences.Object.ContactLastAutoSynchronizationUtcTicks.Should().Be(start.UtcTicks);
        runtime.State.Should().Be(ApplicationRuntimeState.Initialized);
        clock.PeriodicTimersCreated.Should().Be(0);
        await runtime.StopAsync();
    }

    [Fact]
    public async Task SynchronizeOnce_CanceledToken_StopsThePass()
    {
        var account = new MailAccount { Id = Guid.NewGuid(), IsMailAccessGranted = true };
        var accounts = new Mock<IAccountService>();
        accounts.Setup(x => x.GetAccountsAsync()).ReturnsAsync(new List<MailAccount> { account });
        var manager = new Mock<ISynchronizationManager>();
        manager.Setup(x => x.GetAllSynchronizers()).Returns(Array.Empty<IWinoSynchronizerBase>());
        var runtime = CreateRuntime(_ => Task.CompletedTask, new ManualTimeProvider(), accounts, manager: manager);
        await runtime.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await runtime.Invoking(x => x.SynchronizeOnceAsync(cancellation.Token)).Should().ThrowAsync<OperationCanceledException>();

        manager.Verify(x => x.SynchronizeMailAsync(It.IsAny<MailSynchronizationOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        await runtime.StopAsync();
    }

    [Fact]
    public async Task SynchronizeAccountInbox_RunsOneInboxPassForThatAccountOnly()
    {
        var target = new MailAccount { Id = Guid.NewGuid(), IsMailAccessGranted = true };
        var other = new MailAccount { Id = Guid.NewGuid(), IsMailAccessGranted = true };
        var accounts = new Mock<IAccountService>();
        accounts.Setup(x => x.GetAccountsAsync()).ReturnsAsync(new List<MailAccount> { target, other });
        accounts.Setup(x => x.GetAccountAsync(target.Id)).ReturnsAsync(target);
        var manager = new Mock<ISynchronizationManager>();
        manager.Setup(x => x.GetAllSynchronizers()).Returns(Array.Empty<IWinoSynchronizerBase>());
        manager.Setup(x => x.SynchronizeMailAsync(It.IsAny<MailSynchronizationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MailSynchronizationResult.Empty);
        var clock = new ManualTimeProvider();
        var runtime = CreateRuntime(_ => Task.CompletedTask, clock, accounts, manager: manager);

        var succeeded = await runtime.SynchronizeAccountInboxAsync(target.Id);

        succeeded.Should().BeTrue();
        manager.Verify(x => x.SynchronizeMailAsync(It.Is<MailSynchronizationOptions>(o => o.AccountId == target.Id && o.Type == MailSynchronizationType.InboxOnly), It.IsAny<CancellationToken>()), Times.Once);
        manager.Verify(x => x.SynchronizeMailAsync(It.Is<MailSynchronizationOptions>(o => o.AccountId != target.Id), It.IsAny<CancellationToken>()), Times.Never);
        manager.Verify(x => x.SynchronizeCalendarAsync(It.IsAny<CalendarSynchronizationOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        runtime.State.Should().Be(ApplicationRuntimeState.Initialized);
        clock.PeriodicTimersCreated.Should().Be(0);
        await runtime.StopAsync();
    }

    [Fact]
    public async Task SynchronizeAccountInbox_SkipsUnknownAccountsAndAccountsWithoutMailAccess()
    {
        var noMail = new MailAccount { Id = Guid.NewGuid(), IsMailAccessGranted = false };
        var accounts = new Mock<IAccountService>();
        accounts.Setup(x => x.GetAccountsAsync()).ReturnsAsync(new List<MailAccount> { noMail });
        accounts.Setup(x => x.GetAccountAsync(noMail.Id)).ReturnsAsync(noMail);
        var manager = new Mock<ISynchronizationManager>();
        manager.Setup(x => x.GetAllSynchronizers()).Returns(Array.Empty<IWinoSynchronizerBase>());
        var runtime = CreateRuntime(_ => Task.CompletedTask, new ManualTimeProvider(), accounts, manager: manager);

        (await runtime.SynchronizeAccountInboxAsync(noMail.Id)).Should().BeFalse();
        (await runtime.SynchronizeAccountInboxAsync(Guid.NewGuid())).Should().BeFalse();

        manager.Verify(x => x.SynchronizeMailAsync(It.IsAny<MailSynchronizationOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        await runtime.StopAsync();
    }

    private static async Task AdvanceAndSettleAsync(ManualTimeProvider clock, TimeSpan elapsed)
    {
        clock.Advance(elapsed);
        // Loop continuations run on the thread pool; give them a moment to observe the tick.
        await Task.Delay(50);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ApplicationRuntime CreateRuntime(Func<CancellationToken, Task> initialize,
        TimeProvider? clock = null, Mock<IAccountService>? accounts = null,
        Mock<IPreferencesService>? preferences = null, IMessenger? messenger = null,
        Mock<ISynchronizationManager>? manager = null)
    {
        accounts ??= new Mock<IAccountService>();
        if (accounts.Setups.Count == 0)
            accounts.Setup(x => x.GetAccountsAsync()).ReturnsAsync(new List<MailAccount>());
        if (manager == null)
        {
            manager = new Mock<ISynchronizationManager>();
            manager.Setup(x => x.GetAllSynchronizers()).Returns(Array.Empty<IWinoSynchronizerBase>());
        }
        return new ApplicationRuntime(manager.Object, accounts.Object,
            (preferences ?? new Mock<IPreferencesService>()).Object,
            messenger ?? new WeakReferenceMessenger(), Mock.Of<IWinoLogger>(), initialize,
            _ => Task.CompletedTask, clock);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = new();
        private readonly List<(int Count, TaskCompletionSource Signal)> _waiters = new();
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private TaskCompletionSource _changed = Signal();

        public int PeriodicTimersCreated { get { lock (_gate) return _timers.Count(x => x.Period > TimeSpan.Zero); } }
        public TimeSpan[] ActivePeriods { get { lock (_gate) return _timers.Where(x => !x.IsDisposed && x.Period > TimeSpan.Zero).Select(x => x.Period).ToArray(); } }
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state, dueTime, period);
                _timers.Add(timer);
                NotifyChanged();
                foreach (var waiter in _waiters.Where(x => PeriodicTimersCreated >= x.Count).ToArray())
                {
                    waiter.Signal.TrySetResult();
                    _waiters.Remove(waiter);
                }
                return timer;
            }
        }

        public Task WaitForPeriodicTimersAsync(int count)
        {
            lock (_gate)
            {
                if (PeriodicTimersCreated >= count) return Task.CompletedTask;
                var signal = Signal();
                _waiters.Add((count, signal));
                return signal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        public async Task WaitForActivePeriodsAsync(params TimeSpan[] expected)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (ActivePeriods.Order().SequenceEqual(expected.Order())) return;
                    changed = _changed.Task;
                }
                await changed.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private void NotifyChanged()
        {
            var signal = _changed;
            _changed = Signal();
            signal.TrySetResult();
        }

        public void Advance(TimeSpan elapsed)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _now += elapsed;
                due = _timers.Where(x => !x.IsDisposed && x.Next <= _now).ToArray();
                foreach (var timer in due) timer.Next = timer.Period > TimeSpan.Zero ? _now + timer.Period : DateTimeOffset.MaxValue;
            }
            foreach (var timer in due) timer.Invoke();
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
        {
            public TimeSpan Period { get; private set; } = period;
            public DateTimeOffset Next { get; set; } = owner.GetUtcNow() + due;
            public bool IsDisposed { get; private set; }
            public void Invoke() { if (!IsDisposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
            {
                lock (owner._gate)
                {
                    if (IsDisposed) return false;
                    Period = newPeriod;
                    Next = owner._now + dueTime;
                    return true;
                }
            }
            public void Dispose()
            {
                lock (owner._gate)
                {
                    IsDisposed = true;
                    owner.NotifyChanged();
                }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
