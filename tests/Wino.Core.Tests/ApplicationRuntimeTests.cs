using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
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
        await clock.WaitForPeriodicTimersAsync(2);
        clock.PeriodicTimersCreated.Should().Be(2);

        preferences.SetupGet(x => x.EmailSyncIntervalMinutes).Returns(4);
        preferences.Raise(x => x.PreferenceChanged += null, preferences.Object, nameof(IPreferencesService.EmailSyncIntervalMinutes));
        await clock.WaitForPeriodicTimersAsync(3);
        await clock.WaitForActivePeriodsAsync(TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(3));
        clock.ActivePeriods.Should().BeEquivalentTo(new[] { TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(3) });

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

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ApplicationRuntime CreateRuntime(Func<CancellationToken, Task> initialize,
        TimeProvider? clock = null, Mock<IAccountService>? accounts = null,
        Mock<IPreferencesService>? preferences = null, IMessenger? messenger = null)
    {
        accounts ??= new Mock<IAccountService>();
        if (accounts.Setups.Count == 0)
            accounts.Setup(x => x.GetAccountsAsync()).ReturnsAsync(new List<MailAccount>());
        var manager = new Mock<ISynchronizationManager>();
        manager.Setup(x => x.GetAllSynchronizers()).Returns(Array.Empty<IWinoSynchronizerBase>());
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
