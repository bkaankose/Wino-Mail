using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Server;

namespace Wino.SmokeTest.ConsoleApp;

internal sealed class SmokeSynchronizationHost :
    IRecipient<NewMailSynchronizationRequested>,
    IRecipient<NewCalendarSynchronizationRequested>,
    IRecipient<NewContactSynchronizationRequested>,
    IRecipient<NewTaskSynchronizationRequested>,
    IRecipient<CalendarItemAdded>,
    IRecipient<CalendarItemUpdated>,
    IRecipient<CalendarItemDeleted>,
    IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestObservationTimeout = TimeSpan.FromSeconds(5);
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly WeakReferenceMessenger _messenger;
    private readonly object _waiterLock = new();
    private readonly List<MailWaiter> _mailWaiters = [];
    private readonly Dictionary<Guid, TaskCompletionSource<CalendarSynchronizationResult>> _calendarWaiters = [];
    private readonly Dictionary<Guid, TaskCompletionSource<ContactSynchronizationResult>> _contactWaiters = [];
    private readonly Dictionary<Guid, TaskCompletionSource<TaskSynchronizationResult>> _taskWaiters = [];
    private readonly Dictionary<Guid, SemaphoreSlim> _accountGates = [];
    private Guid? _observedCalendarAccountId;
    private CalendarChangeSummary _calendarChanges;
    private bool _disposed;

    public SmokeSynchronizationHost(ISynchronizationManager synchronizationManager)
    {
        _synchronizationManager = synchronizationManager;
        _messenger = WeakReferenceMessenger.Default;
        _messenger.RegisterAll(this);
    }

    public CalendarChangeSummary LastCalendarChanges
    {
        get
        {
            lock (_waiterLock)
                return _calendarChanges;
        }
    }

    public Task<MailSynchronizationResult> SynchronizeMailAsync(
        MailSynchronizationOptions options,
        CancellationToken cancellationToken)
        => AwaitMailRequestAsync(
            options.AccountId,
            options.Type,
            options.Id,
            () =>
            {
                _messenger.Send(new NewMailSynchronizationRequested(options));
                return Task.CompletedTask;
            },
            cancellationToken);

    public Task<MailSynchronizationResult> ExecuteMailOperationAsync(
        Guid accountId,
        Func<Task> operation,
        CancellationToken cancellationToken)
        => AwaitMailRequestAsync(accountId, MailSynchronizationType.ExecuteRequests, null, operation, cancellationToken);

    public async Task<CalendarSynchronizationResult> SynchronizeCalendarAsync(
        CalendarSynchronizationOptions options,
        CancellationToken cancellationToken)
    {
        var completion = NewCompletion<CalendarSynchronizationResult>();
        lock (_waiterLock)
        {
            _calendarWaiters.Add(options.Id, completion);
            _observedCalendarAccountId = options.AccountId;
            _calendarChanges = default;
        }

        try
        {
            _messenger.Send(new NewCalendarSynchronizationRequested(options));
            return await completion.Task.WaitAsync(DefaultTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_waiterLock)
            {
                _calendarWaiters.Remove(options.Id);
                _observedCalendarAccountId = null;
            }
        }
    }

    public async Task<ContactSynchronizationResult> SynchronizeContactsAsync(
        ContactSynchronizationOptions options,
        CancellationToken cancellationToken)
    {
        var completion = NewCompletion<ContactSynchronizationResult>();
        lock (_waiterLock)
            _contactWaiters.Add(options.Id, completion);

        try
        {
            _messenger.Send(new NewContactSynchronizationRequested(options));
            return await completion.Task.WaitAsync(DefaultTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_waiterLock)
                _contactWaiters.Remove(options.Id);
        }
    }

    public async Task<TaskSynchronizationResult> SynchronizeTasksAsync(
        TaskSynchronizationOptions options,
        CancellationToken cancellationToken)
    {
        var completion = NewCompletion<TaskSynchronizationResult>();
        lock (_waiterLock)
            _taskWaiters.Add(options.Id, completion);

        try
        {
            _messenger.Send(new NewTaskSynchronizationRequested(options));
            return await completion.Task.WaitAsync(DefaultTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_waiterLock)
                _taskWaiters.Remove(options.Id);
        }
    }

    public void Receive(NewMailSynchronizationRequested message)
        => _ = ProcessMailAsync(message);

    public void Receive(NewCalendarSynchronizationRequested message)
        => _ = ProcessCalendarAsync(message);

    public void Receive(NewContactSynchronizationRequested message)
        => _ = ProcessContactsAsync(message);

    public void Receive(NewTaskSynchronizationRequested message)
        => _ = ProcessTasksAsync(message);

    public void Receive(CalendarItemAdded message)
        => CountCalendarChange(message.CalendarItem.AssignedCalendar?.AccountId, CalendarChangeKind.Added, message.Source);

    public void Receive(CalendarItemUpdated message)
        => CountCalendarChange(message.CalendarItem.AssignedCalendar?.AccountId, CalendarChangeKind.Updated, message.Source);

    public void Receive(CalendarItemDeleted message)
        => CountCalendarChange(message.CalendarItem.AssignedCalendar?.AccountId, CalendarChangeKind.Deleted, message.Source);

    private async Task<MailSynchronizationResult> AwaitMailRequestAsync(
        Guid accountId,
        MailSynchronizationType type,
        Guid? requestId,
        Func<Task> trigger,
        CancellationToken cancellationToken)
    {
        var waiter = new MailWaiter(
            accountId,
            type,
            requestId,
            NewCompletion<bool>(),
            NewCompletion<MailSynchronizationResult>());
        lock (_waiterLock)
            _mailWaiters.Add(waiter);

        try
        {
            await trigger().ConfigureAwait(false);
            await waiter.Observed.Task.WaitAsync(RequestObservationTimeout, cancellationToken).ConfigureAwait(false);
            return await waiter.Completion.Task.WaitAsync(DefaultTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_waiterLock)
                _mailWaiters.Remove(waiter);
        }
    }

    private async Task ProcessMailAsync(NewMailSynchronizationRequested message)
    {
        MailWaiter? observedWaiter;
        lock (_waiterLock)
            observedWaiter = FindMailWaiter(message.Options);
        observedWaiter?.Observed.TrySetResult(true);

        var gate = GetAccountGate(message.Options.AccountId);
        await gate.WaitAsync().ConfigureAwait(false);

        MailSynchronizationResult result;
        try
        {
            result = await _synchronizationManager.SynchronizeMailAsync(message.Options).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = MailSynchronizationResult.Failed(exception);
        }
        finally
        {
            gate.Release();
        }

        MailWaiter? waiter;
        lock (_waiterLock)
            waiter = FindMailWaiter(message.Options);
        waiter?.Completion.TrySetResult(result);
    }

    private async Task ProcessCalendarAsync(NewCalendarSynchronizationRequested message)
    {
        var gate = GetAccountGate(message.Options.AccountId);
        await gate.WaitAsync().ConfigureAwait(false);

        CalendarSynchronizationResult result;
        try
        {
            result = await _synchronizationManager.SynchronizeCalendarAsync(message.Options).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = CalendarSynchronizationResult.Failed(exception);
        }
        finally
        {
            gate.Release();
        }

        TaskCompletionSource<CalendarSynchronizationResult>? completion;
        lock (_waiterLock)
            _calendarWaiters.TryGetValue(message.Options.Id, out completion);
        completion?.TrySetResult(result);
    }

    private async Task ProcessContactsAsync(NewContactSynchronizationRequested message)
    {
        var gate = GetAccountGate(message.Options.AccountId);
        await gate.WaitAsync().ConfigureAwait(false);

        ContactSynchronizationResult result;
        try
        {
            result = await _synchronizationManager.SynchronizeContactsAsync(message.Options).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = ContactSynchronizationResult.Failed(exception);
        }
        finally
        {
            gate.Release();
        }

        TaskCompletionSource<ContactSynchronizationResult>? completion;
        lock (_waiterLock)
            _contactWaiters.TryGetValue(message.Options.Id, out completion);
        completion?.TrySetResult(result);
    }

    private async Task ProcessTasksAsync(NewTaskSynchronizationRequested message)
    {
        var gate = GetAccountGate(message.Options.AccountId);
        await gate.WaitAsync().ConfigureAwait(false);

        TaskSynchronizationResult result;
        try
        {
            result = await _synchronizationManager.SynchronizeTasksAsync(message.Options).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            result = TaskSynchronizationResult.Failed(exception);
        }
        finally
        {
            gate.Release();
        }

        TaskCompletionSource<TaskSynchronizationResult>? completion;
        lock (_waiterLock)
            _taskWaiters.TryGetValue(message.Options.Id, out completion);
        completion?.TrySetResult(result);
    }

    private SemaphoreSlim GetAccountGate(Guid accountId)
    {
        lock (_waiterLock)
        {
            if (_accountGates.TryGetValue(accountId, out var existing))
                return existing;

            var created = new SemaphoreSlim(1, 1);
            _accountGates.Add(accountId, created);
            return created;
        }
    }

    private MailWaiter? FindMailWaiter(MailSynchronizationOptions options)
        => _mailWaiters.FirstOrDefault(candidate =>
               candidate.AccountId == options.AccountId &&
               candidate.Type == options.Type &&
               candidate.RequestId == options.Id)
           ?? _mailWaiters.FirstOrDefault(candidate =>
               candidate.AccountId == options.AccountId &&
               candidate.Type == options.Type &&
               candidate.RequestId is null);

    private void CountCalendarChange(Guid? accountId, CalendarChangeKind kind, EntityUpdateSource source)
    {
        if (source != EntityUpdateSource.Server)
            return;

        lock (_waiterLock)
        {
            if (accountId != _observedCalendarAccountId)
                return;

            _calendarChanges = kind switch
            {
                CalendarChangeKind.Added => _calendarChanges with { Added = _calendarChanges.Added + 1 },
                CalendarChangeKind.Updated => _calendarChanges with { Updated = _calendarChanges.Updated + 1 },
                CalendarChangeKind.Deleted => _calendarChanges with { Deleted = _calendarChanges.Deleted + 1 },
                _ => _calendarChanges
            };
        }
    }

    private static TaskCompletionSource<T> NewCompletion<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _messenger.UnregisterAll(this);

        lock (_waiterLock)
        {
            foreach (var waiter in _mailWaiters)
            {
                waiter.Observed.TrySetCanceled();
                waiter.Completion.TrySetCanceled();
            }
            foreach (var completion in _calendarWaiters.Values)
                completion.TrySetCanceled();
            foreach (var completion in _contactWaiters.Values)
                completion.TrySetCanceled();
            foreach (var completion in _taskWaiters.Values)
                completion.TrySetCanceled();
            foreach (var gate in _accountGates.Values)
                gate.Dispose();
        }
    }

    private sealed record MailWaiter(
        Guid AccountId,
        MailSynchronizationType Type,
        Guid? RequestId,
        TaskCompletionSource<bool> Observed,
        TaskCompletionSource<MailSynchronizationResult> Completion);

    private enum CalendarChangeKind
    {
        Added,
        Updated,
        Deleted
    }
}

internal readonly record struct CalendarChangeSummary(int Added, int Updated, int Deleted);
