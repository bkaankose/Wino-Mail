#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Settings;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.Client.Shell;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Core.Services;

public sealed class ApplicationRuntime : IApplicationRuntime,
    IRecipient<NewMailSynchronizationRequested>, IRecipient<NewCalendarSynchronizationRequested>,
    IRecipient<NewContactSynchronizationRequested>, IRecipient<NewTaskSynchronizationRequested>,
    IRecipient<AccountCreatedMessage>, IRecipient<AccountRemovedMessage>, IRecipient<AccountUpdatedMessage>,
    IAsyncDisposable
{
    private const int InboxSyncsPerFullSync = 20;
    private static readonly TimeSpan RareModeSynchronizationInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RareModeDueCheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan InboxRerunPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan InboxRerunWaitLimit = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IPreferencesService _preferencesService;
    private readonly IPlatformCapabilities _platformCapabilities;
    private readonly IAccountService _accountService;
    private readonly IMessenger _messenger;
    private readonly IWinoLogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<CancellationToken, Task> _initializeServices;
    private readonly Func<CancellationToken, Task> _backgroundStartup;
    private readonly ISyncCoordinator _coordinator;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _autoSynchronizationSemaphore = new(1, 1);
    // When each account last had a FullFolders pass. Time-based rather than tick-based, because covered accounts
    // skip ticks and would otherwise never reach a full pass.
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastFullFolderSyncUtc = new();
    private readonly ConcurrentDictionary<int, Task> _jobs = new();
    private readonly Queue<ApplicationRuntimeState> _stateChanges = new();
    private readonly HashSet<Guid> _pendingCreatedAccounts = new();
    // One queued inbox pass per account for pushes that arrive while that account synchronizes. Guarded by _gate.
    private readonly Dictionary<Guid, Task<bool>> _pendingInboxReruns = new();
    private bool _dispatchingStateChanges;
    private int _jobId;
    private Task? _initialization;
    private Task? _stopping;
    private CancellationTokenSource? _mailLoop;
    private CancellationTokenSource? _calendarLoop;
    private CancellationTokenSource? _taskLoop;
    private CancellationTokenSource? _contactLoop;
    private Task? _startupPass;
    private bool _hasAccounts;
    private bool _subscribed;
    private ApplicationRuntimeState _state;

    public ApplicationRuntime(ISynchronizationManager synchronizationManager, IAccountService accountService,
        IPreferencesService preferencesService, IPlatformCapabilities platformCapabilities, IMessenger messenger, IWinoLogger logger,
        Func<CancellationToken, Task> initializeServices, Func<CancellationToken, Task> backgroundStartup,
        ISyncCoordinator coordinator, TimeProvider? timeProvider = null)
    {
        _coordinator = coordinator;
        _synchronizationManager = synchronizationManager;
        _accountService = accountService;
        _preferencesService = preferencesService;
        _platformCapabilities = platformCapabilities;
        _messenger = messenger;
        _logger = logger;
        _initializeServices = initializeServices;
        _backgroundStartup = backgroundStartup;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ApplicationRuntimeState State { get { lock (_gate) return _state; } }
    public event EventHandler<ApplicationRuntimeState>? StateChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task initialization;
        lock (_gate)
        {
            ThrowIfTerminal();
            if (_state is ApplicationRuntimeState.Initialized or ApplicationRuntimeState.Running)
                return Task.CompletedTask;

            // Start on the pool so an immediately completing callback cannot finish and
            // clear the shared task before the assignment has published it.
            initialization = _initialization ??= Task.Run(InitializeCoreAsync);
        }

        return initialization.WaitAsync(cancellationToken);
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            await _initializeServices(_lifetime.Token).ConfigureAwait(false);
            await RefreshAccountsAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                RegisterRecipients();
                ChangeState(ApplicationRuntimeState.Initialized);
                Track(() => _backgroundStartup(_lifetime.Token));
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                UnregisterRecipients();
                if (_state is not (ApplicationRuntimeState.Stopping or ApplicationRuntimeState.Stopped))
                    ChangeState(ex is RuntimeInitializationFaultException
                        ? ApplicationRuntimeState.Faulted : ApplicationRuntimeState.Uninitialized);

                _initialization = null;
            }
            throw;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            ThrowIfTerminal();
            ChangeState(ApplicationRuntimeState.Running);
            EnsureLoops();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task stopping;
        lock (_gate)
        {
            if (_stopping == null)
            {
                ChangeState(ApplicationRuntimeState.Stopping);
                _lifetime.Cancel();
                UnregisterRecipients();
                StopLoops();
            }
            stopping = _stopping ??= Task.Run(StopCoreAsync);
        }
        return stopping.WaitAsync(cancellationToken);
    }

    private async Task StopCoreAsync()
    {
        Task[] jobs;
        lock (_gate)
        {
            jobs = _jobs.Values.Concat(_initialization is null ? Array.Empty<Task>() : new[] { _initialization }).ToArray();
        }

        try
        {
            // A runtime stopped before initialization step 2 completed has no synchronizers to drain,
            // and the manager throws rather than returning an empty set in that state.
            IEnumerable<IWinoSynchronizerBase> synchronizers;
            try
            {
                synchronizers = _synchronizationManager.GetAllSynchronizers();
            }
            catch (InvalidOperationException)
            {
                synchronizers = Array.Empty<IWinoSynchronizerBase>();
            }

            var cancellation = synchronizers
                .Select(x => _synchronizationManager.CancelSynchronizationsAsync(x.Account.Id));
            // Stopped means the runtime has actually drained. A timeout must never
            // authorize native process termination while provider work is still running.
            await Task.WhenAll(jobs.Concat(cancellation)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.CaptureException(ex, "ApplicationRuntimeStop");
            lock (_gate) ChangeState(ApplicationRuntimeState.Faulted);
            throw;
        }

        lock (_gate) ChangeState(ApplicationRuntimeState.Stopped);
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void ThrowIfTerminal()
    {
        if (_state is ApplicationRuntimeState.Stopping or ApplicationRuntimeState.Stopped or ApplicationRuntimeState.Faulted)
            throw new InvalidOperationException($"The application runtime cannot start in state {_state}.");
    }

    private void ChangeState(ApplicationRuntimeState state)
    {
        if (_state == state) return;
        _state = state;
        _stateChanges.Enqueue(state);
        if (_dispatchingStateChanges) return;
        _dispatchingStateChanges = true;
        _ = Task.Run(DispatchStateChanges);
    }

    private void DispatchStateChanges()
    {
        while (true)
        {
            ApplicationRuntimeState state;
            lock (_gate)
            {
                if (_stateChanges.Count == 0)
                {
                    _dispatchingStateChanges = false;
                    return;
                }
                state = _stateChanges.Dequeue();
            }

            // Notify in transition order, outside the lifecycle lock. A subscriber can
            // stop and await the runtime without holding startup or shutdown hostage.
            try { StateChanged?.Invoke(this, state); }
            catch (Exception ex) { _logger.CaptureException(ex, "ApplicationRuntimeStateChanged"); }
        }
    }

    private void RegisterRecipients()
    {
        if (_subscribed) return;
        _messenger.Register<NewMailSynchronizationRequested>(this);
        _messenger.Register<NewCalendarSynchronizationRequested>(this);
        _messenger.Register<NewContactSynchronizationRequested>(this);
        _messenger.Register<NewTaskSynchronizationRequested>(this);
        _messenger.Register<AccountCreatedMessage>(this);
        _messenger.Register<AccountRemovedMessage>(this);
        _messenger.Register<AccountUpdatedMessage>(this);
        _preferencesService.PreferenceChanged += PreferencesChanged;
        _coordinator.CatchUpRequested += CatchUpRequested;
        _subscribed = true;
    }

    private void UnregisterRecipients()
    {
        _messenger.UnregisterAll(this);
        if (_subscribed)
        {
            _preferencesService.PreferenceChanged -= PreferencesChanged;
            _coordinator.CatchUpRequested -= CatchUpRequested;
        }
        _subscribed = false;
    }

    // The coordinator decided that live coverage was lost long enough, or came back after a long enough outage,
    // that one bounded mail pass is worth it. The loops themselves are not restarted: their intervals no longer
    // depend on the relay, and each tick asks the coordinator per account.
    private void CatchUpRequested(object? sender, SyncCatchUpReason reason)
    {
        lock (_gate)
        {
            if (_state != ApplicationRuntimeState.Running || !_hasAccounts) return;
            Track(() => ExecuteAutoSynchronizationAsync(_lifetime.Token));
        }
    }

    private void Track(Func<Task> operation)
    {
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested) return;
            var id = ++_jobId;
            var job = Task.Run(async () =>
            {
                try
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    await operation().ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _logger.CaptureException(ex, "ApplicationRuntimeWork"); }
            });
            _jobs[id] = job;
            _ = job.ContinueWith(_ => _jobs.TryRemove(id, out var ignored), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public void Receive(AccountCreatedMessage message)
    {
        lock (_gate)
        {
            _hasAccounts = true;
            _pendingCreatedAccounts.Add(message.Account.Id);
        }
        // Initial account work starts only after the host's shell handoff.
    }

    public void Receive(AccountRemovedMessage message)
    {
        lock (_gate) _pendingCreatedAccounts.Remove(message.Account.Id);
        Track(RefreshAccountsAsync);
    }
    public void Receive(AccountUpdatedMessage message) => Track(async () =>
    {
        await RefreshAccountsAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (_pendingCreatedAccounts.Count == 0) EnsureLoops();
        }
    });

    private async Task RefreshAccountsAsync()
    {
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        bool hasAccounts;
        lock (_gate)
        {
            hasAccounts = _hasAccounts = accounts.Any();
            var ids = accounts.Select(x => x.Id).ToHashSet();
            foreach (var id in _lastFullFolderSyncUtc.Keys.Where(x => !ids.Contains(x)))
                _lastFullFolderSyncUtc.TryRemove(id, out _);

            if (!_hasAccounts) StopLoops();
        }

        // With every account gone the app returns to the welcome window. The next account starts
        // from its own initial synchronization, so the background windows start over too.
        if (!hasAccounts)
            ResetRareModeSynchronizationDates();
    }

    private void ResetRareModeSynchronizationDates()
    {
        if (_preferencesService.TaskLastAutoSynchronizationUtcTicks != 0)
            _preferencesService.TaskLastAutoSynchronizationUtcTicks = 0;
        if (_preferencesService.ContactLastAutoSynchronizationUtcTicks != 0)
            _preferencesService.ContactLastAutoSynchronizationUtcTicks = 0;
    }

    private void PreferencesChanged(object? sender, string propertyName)
    {
        lock (_gate)
        {
            if (_state != ApplicationRuntimeState.Running || !_hasAccounts) return;
            if (!SettingAvailability.IsAvailable(PlatformSetting.SyncIntervals, _platformCapabilities)) return;
            if (propertyName == nameof(IPreferencesService.EmailSyncIntervalMinutes)) RestartMailLoop();
            if (propertyName == nameof(IPreferencesService.CalendarSyncIntervalMinutes)) RestartCalendarLoop();
        }
    }

    private void EnsureLoops()
    {
        if (_state != ApplicationRuntimeState.Running || !_hasAccounts || _lifetime.IsCancellationRequested) return;
        if (_startupPass == null) StartStartupPass();
        if (_mailLoop == null) RestartMailLoop();
        if (_calendarLoop == null) RestartCalendarLoop();
        if (_taskLoop == null) RestartTaskLoop();
        if (_contactLoop == null) RestartContactLoop();
    }

    // The intervals come from the coordinator: the user's settings on desktop (in timer mode and as the relay
    // fallback alike), the defaults on mobile. The relay never changes them; it only lets covered accounts skip ticks.
    private void RestartMailLoop()
    {
        _mailLoop?.Cancel();
        var source = _mailLoop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var startupPass = PendingStartupPass;
        var interval = _coordinator.MailSyncInterval;
        Track(() => RunLoopWithCleanupAsync(source, () => RunAutoSynchronizationLoopAsync(interval, startupPass, source.Token)));
    }

    private void RestartCalendarLoop()
    {
        _calendarLoop?.Cancel();
        var source = _calendarLoop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var startupPass = PendingStartupPass;
        var interval = _coordinator.CalendarSyncInterval;
        Track(() => RunLoopWithCleanupAsync(source, () => RunCalendarAutoSynchronizationLoopAsync(interval, startupPass, source.Token)));
    }

    /// <summary>The startup pass while it still runs; a loop created meanwhile lets it cover the first run.</summary>
    private Task? PendingStartupPass => _startupPass is { IsCompleted: false } ? _startupPass : null;

    /// <summary>
    /// The first synchronization after launch runs once, in priority order: mail, then calendar.
    /// To Do and People keep their own six-hour schedule. Each loop used to run its own first pass
    /// at the same time, and mail gave up its pass whenever the calendar pass held the shared
    /// semaphore, so a background launch showed no new mail until the first timer tick minutes later.
    /// </summary>
    private void StartStartupPass()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _startupPass = completion.Task;
        Track(async () =>
        {
            try
            {
                await RunStartupStepAsync(ExecuteAutoSynchronizationAsync, "mail").ConfigureAwait(false);
                await RunStartupStepAsync(ExecuteCalendarAutoSynchronizationAsync, "calendar").ConfigureAwait(false);
            }
            finally
            {
                completion.TrySetResult();
            }
        });
    }

    private async Task RunStartupStepAsync(Func<CancellationToken, Task> step, string name)
    {
        _lifetime.Token.ThrowIfCancellationRequested();
        try
        {
            await step(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Information($"Startup {name} synchronization failed: {ex.Message}");
        }
    }

    // Tasks and contacts are rarely used modes, and Google Tasks shares one daily quota across
    // every installation. They synchronize in the background once every six hours, measured from
    // a persisted timestamp so short sessions still catch up. Nothing runs at launch; the loop
    // only checks whether a run is due, and a new installation starts its first window now.
    private void RestartTaskLoop()
    {
        _taskLoop?.Cancel();
        var source = _taskLoop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Track(() => RunLoopWithCleanupAsync(source, () => RunRareModeLoopAsync(
            () => _preferencesService.TaskLastAutoSynchronizationUtcTicks,
            ticks => _preferencesService.TaskLastAutoSynchronizationUtcTicks = ticks,
            ExecuteTaskAutoSynchronizationAsync, "task", source.Token)));
    }

    private void RestartContactLoop()
    {
        _contactLoop?.Cancel();
        var source = _contactLoop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Track(() => RunLoopWithCleanupAsync(source, () => RunRareModeLoopAsync(
            () => _preferencesService.ContactLastAutoSynchronizationUtcTicks,
            ticks => _preferencesService.ContactLastAutoSynchronizationUtcTicks = ticks,
            ExecuteContactAutoSynchronizationAsync, "contact", source.Token)));
    }

    private async Task RunLoopWithCleanupAsync(CancellationTokenSource source, Func<Task> operation)
    {
        try { await operation().ConfigureAwait(false); }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_mailLoop, source)) _mailLoop = null;
                if (ReferenceEquals(_calendarLoop, source)) _calendarLoop = null;
                if (ReferenceEquals(_taskLoop, source)) _taskLoop = null;
                if (ReferenceEquals(_contactLoop, source)) _contactLoop = null;
            }
            source.Dispose();
        }
    }

    private void StopLoops()
    {
        _mailLoop?.Cancel();
        _calendarLoop?.Cancel();
        _taskLoop?.Cancel();
        _contactLoop?.Cancel();
        _mailLoop = null;
        _calendarLoop = null;
        _taskLoop = null;
        _contactLoop = null;
    }

    public void Receive(NewMailSynchronizationRequested message)
        => Track(() => HandleMailSynchronizationRequestedAsync(message));

    private async Task HandleMailSynchronizationRequestedAsync(NewMailSynchronizationRequested message)
    {
        var synchronizationManager = _synchronizationManager;
        if (synchronizationManager == null)
            return;

        MailSynchronizationResult syncResult;

        try
        {
            // Messenger recipients run synchronously on the sender's thread. Mail actions are
            // commonly requested by the UI thread, so force the synchronous setup/batching
            // portion of synchronization onto the thread pool as well as its async continuations.
            syncResult = await Task
                .Run(() => synchronizationManager.SynchronizeMailAsync(message.Options, _lifetime.Token), _lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            syncResult = MailSynchronizationResult.Canceled;
        }
        catch (Exception ex)
        {
            // Defensive fallback to guarantee completion message emission.
            Log.Error(ex, "Mail synchronization request failed for account {AccountId}", message.Options.AccountId);
            syncResult = MailSynchronizationResult.Failed(ex);
        }

        _messenger.Send(new AccountSynchronizationCompleted(
            message.Options.AccountId,
            syncResult.CompletedState,
            message.Options.GroupedSynchronizationTrackingId,
            message.Options.Type));

        if (syncResult.CompletedState is SynchronizationCompletedState.Success or SynchronizationCompletedState.PartiallyCompleted)
        {
            await ClearInvalidCredentialAttentionIfNeededAsync(message.Options.AccountId).ConfigureAwait(false);

        }

        if (syncResult.CompletedState == SynchronizationCompletedState.Failed ||
            syncResult.CompletedState == SynchronizationCompletedState.PartiallyCompleted)
        {
            var errorMessage = GetSynchronizationFailureMessage(message.Options.Type, syncResult.AllIssues, syncResult.Exception?.Message);
            var severity = syncResult.CompletedState == SynchronizationCompletedState.PartiallyCompleted
                ? InfoBarMessageType.Warning
                : InfoBarMessageType.Error;

            QueueSynchronizationFailure(errorMessage, severity);
        }
    }

    public void Receive(NewCalendarSynchronizationRequested message)
        => Track(() => HandleCalendarSynchronizationRequestedAsync(message));

    private async Task HandleCalendarSynchronizationRequestedAsync(NewCalendarSynchronizationRequested message)
    {
        var synchronizationManager = _synchronizationManager;
        if (synchronizationManager == null)
            return;

        CalendarSynchronizationResult calendarSyncResult;
        try
        {
            calendarSyncResult = await Task
                .Run(() => synchronizationManager.SynchronizeCalendarAsync(message.Options, _lifetime.Token), _lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            calendarSyncResult = CalendarSynchronizationResult.Canceled;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Calendar synchronization request failed for account {AccountId}", message.Options.AccountId);
            calendarSyncResult = CalendarSynchronizationResult.Failed(ex);
        }

        if (calendarSyncResult.CompletedState is SynchronizationCompletedState.Failed or SynchronizationCompletedState.PartiallyCompleted)
        {
            QueueSynchronizationFailure(
                GetCalendarSynchronizationFailureMessage(message.Options.Type, calendarSyncResult.AllIssues, calendarSyncResult.Exception?.Message),
                calendarSyncResult.CompletedState == SynchronizationCompletedState.PartiallyCompleted
                    ? InfoBarMessageType.Warning
                    : InfoBarMessageType.Error);
        }
    }

    public void Receive(NewContactSynchronizationRequested message)
        => Track(() => HandleContactSynchronizationRequestedAsync(message));

    private async Task HandleContactSynchronizationRequestedAsync(NewContactSynchronizationRequested message)
    {
        var synchronizationManager = _synchronizationManager;
        if (synchronizationManager == null)
            return;

        ContactSynchronizationResult syncResult;
        try
        {
            syncResult = await Task
                .Run(() => synchronizationManager.SynchronizeContactsAsync(message.Options, _lifetime.Token), _lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Contact synchronization request failed for account {AccountId}", message.Options.AccountId);
            syncResult = ContactSynchronizationResult.Failed(ex);
        }

        if (syncResult.CompletedState is SynchronizationCompletedState.Failed or SynchronizationCompletedState.PartiallyCompleted)
        {
            QueueSynchronizationFailure(
                GetSynchronizationFailureMessage(syncResult.Issues, syncResult.Exception?.Message, Translator.Exception_FailedToSynchronizeContacts),
                syncResult.CompletedState == SynchronizationCompletedState.PartiallyCompleted
                    ? InfoBarMessageType.Warning
                    : InfoBarMessageType.Error);
        }
    }

    public void Receive(NewTaskSynchronizationRequested message)
        => Track(() => HandleTaskSynchronizationRequestedAsync(message));

    private async Task HandleTaskSynchronizationRequestedAsync(NewTaskSynchronizationRequested message)
    {
        var synchronizationManager = _synchronizationManager;
        if (synchronizationManager == null)
            return;

        TaskSynchronizationResult syncResult;
        try
        {
            syncResult = await Task
                .Run(() => synchronizationManager.SynchronizeTasksAsync(message.Options, _lifetime.Token), _lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Task synchronization request failed for account {AccountId}", message.Options.AccountId);
            syncResult = TaskSynchronizationResult.Failed(ex);
        }

        if (syncResult.CompletedState is SynchronizationCompletedState.Failed or SynchronizationCompletedState.PartiallyCompleted)
        {
            QueueSynchronizationFailure(
                GetSynchronizationFailureMessage(syncResult.Issues, syncResult.Exception?.Message, Translator.Exception_FailedToSynchronizeTasks),
                syncResult.CompletedState == SynchronizationCompletedState.PartiallyCompleted
                    ? InfoBarMessageType.Warning
                    : InfoBarMessageType.Error);
        }
    }

    public async Task SynchronizeCreatedAccountAsync(Wino.Core.Domain.Entities.Shared.MailAccount account, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        Task work;
        lock (_gate)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            // Start every granted mode at once. Each handler hops to the thread pool, and each mode
            // has its own per-account gate, so contacts and To Do never wait for the initial mail
            // download. The caller still waits until all of them finish.
            var synchronizations = new List<Task>(4);

            if (account.IsMailAccessGranted)
            {
                synchronizations.Add(HandleMailSynchronizationRequestedAsync(new NewMailSynchronizationRequested(new MailSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = MailSynchronizationType.FullFolders
                })));
            }

            if (account.IsCalendarAccessGranted)
            {
                synchronizations.Add(HandleCalendarSynchronizationRequestedAsync(new NewCalendarSynchronizationRequested(new CalendarSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = CalendarSynchronizationType.CalendarEvents
                })));
            }

            if (account.IsContactAccessGranted)
            {
                synchronizations.Add(HandleContactSynchronizationRequestedAsync(new NewContactSynchronizationRequested(new ContactSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = ContactSynchronizationType.Delta
                })));
            }

            if (account.IsTaskAccessGranted && !account.IsTaskReauthorizationRequired)
            {
                synchronizations.Add(HandleTaskSynchronizationRequestedAsync(new NewTaskSynchronizationRequested(new TaskSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = TaskSynchronizationType.Delta
                })));
            }

            work = Task.WhenAll(synchronizations);
            // Initial account work is tracked too, so process shutdown can wait for it.
            Track(() => work);
        }
        await work.WaitAsync(cancellationToken).ConfigureAwait(false);
        await RefreshAccountsAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _pendingCreatedAccounts.Remove(account.Id);
            EnsureLoops();
        }
    }

    public async Task SynchronizeOnceAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        Task work;
        lock (_gate)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            // The pass reuses the loop bodies without their timers. It is tracked so shutdown
            // waits for it, and it stops on either the caller's token or the runtime lifetime.
            // Run on the pool so provider work never starts while the lifecycle lock is held.
            work = Task.Run(() => RunSynchronizationPassAsync(cancellationToken), CancellationToken.None);
            Track(() => work);
        }
        await work.ConfigureAwait(false);
    }

    public async Task<bool> SynchronizeAccountInboxAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
        if (account is null || !account.IsMailAccessGranted)
            return false;

        Task<bool> work;
        lock (_gate)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            // Tracked like the bounded pass, and run on the pool so provider work never starts under the lifecycle lock.
            work = Task.Run(() => RunAccountInboxPassAsync(account.Id, cancellationToken), CancellationToken.None);
            Track(() => work);
        }

        return await work.ConfigureAwait(false);
    }

    public async Task<bool> FlushQueuedRequestsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        Task<bool> work;
        lock (_gate)
        {
            _lifetime.Token.ThrowIfCancellationRequested();
            // Tracked like the bounded pass, and run on the pool so provider work never starts under the lifecycle lock.
            work = Task.Run(() => RunQueuedRequestsPassAsync(cancellationToken), CancellationToken.None);
            Track(() => work);
        }

        return await work.ConfigureAwait(false);
    }

    private async Task<bool> RunQueuedRequestsPassAsync(CancellationToken cancellationToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var token = source.Token;

        foreach (var synchronizer in _synchronizationManager.GetAllSynchronizers().ToArray())
        {
            token.ThrowIfCancellationRequested();
            if (!synchronizer.HasQueuedRequests())
                continue;

            var accountId = synchronizer.Account.Id;
            // Each kind runs only when it has queued requests: an ExecuteRequests run without them
            // would fall through to an ordinary synchronization of that kind.
            if (synchronizer.HasQueuedRequests(static request => request is IMailActionRequest or IFolderActionRequest or ICategoryActionRequest))
            {
                await RunPassStepAsync(ct => _synchronizationManager.SynchronizeMailAsync(new MailSynchronizationOptions
                {
                    AccountId = accountId,
                    Type = MailSynchronizationType.ExecuteRequests
                }, ct), "queued mail request", token).ConfigureAwait(false);
            }

            if (synchronizer.HasQueuedRequests(static request => request is ICalendarActionRequest))
            {
                await RunPassStepAsync(ct => _synchronizationManager.SynchronizeCalendarAsync(new CalendarSynchronizationOptions
                {
                    AccountId = accountId,
                    Type = CalendarSynchronizationType.ExecuteRequests
                }, ct), "queued calendar request", token).ConfigureAwait(false);
            }

            if (synchronizer.HasQueuedRequests(static request => request is IContactActionRequest))
            {
                await RunPassStepAsync(ct => _synchronizationManager.SynchronizeContactsAsync(new ContactSynchronizationOptions
                {
                    AccountId = accountId,
                    Type = ContactSynchronizationType.ExecuteRequests
                }, ct), "queued contact request", token).ConfigureAwait(false);
            }

            if (synchronizer.HasQueuedRequests(static request => request is ITaskActionRequest))
            {
                await RunPassStepAsync(ct => _synchronizationManager.SynchronizeTasksAsync(new TaskSynchronizationOptions
                {
                    AccountId = accountId,
                    Type = TaskSynchronizationType.ExecuteRequests
                }, ct), "queued task request", token).ConfigureAwait(false);
            }
        }

        return !_synchronizationManager.GetAllSynchronizers().Any(synchronizer => synchronizer.HasQueuedRequests());
    }

    private async Task<bool> RunAccountInboxPassAsync(Guid accountId, CancellationToken cancellationToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var token = source.Token;
        token.ThrowIfCancellationRequested();

        // A running synchronization may have read the provider's changes before the one this push reports.
        // One more inbox pass follows it; pushes that arrive in the meantime share that pass.
        if (_synchronizationManager.IsAccountSynchronizing(accountId))
        {
            var rerun = QueueInboxRerun(accountId);
            try
            {
                return await rerun.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_lifetime.Token.IsCancellationRequested)
            {
                // The caller's budget ran out while the running sync goes on; the queued pass still follows it.
                return true;
            }
        }

        return await RunInboxPassCoreAsync(accountId, token).ConfigureAwait(false);
    }

    /// <summary>
    /// The pending rerun for the account, started when none is pending. It runs on the runtime lifetime, so a caller
    /// that stops waiting does not cancel it for the others.
    /// </summary>
    private Task<bool> QueueInboxRerun(Guid accountId)
    {
        lock (_gate)
        {
            if (_pendingInboxReruns.TryGetValue(accountId, out var pending))
                return pending;

            var rerun = Task.Run(() => RunInboxRerunAsync(accountId, _lifetime.Token), CancellationToken.None);
            _pendingInboxReruns[accountId] = rerun;
            Track(() => rerun);
            return rerun;
        }
    }

    private async Task<bool> RunInboxRerunAsync(Guid accountId, CancellationToken cancellationToken)
    {
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(InboxRerunWaitLimit);
            while (_synchronizationManager.IsAccountSynchronizing(accountId))
                await Task.Delay(InboxRerunPollInterval, _timeProvider, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Information("Account {AccountId} is still synchronizing after {Limit}; the queued inbox pass is skipped.", accountId, InboxRerunWaitLimit);
            return false;
        }
        finally
        {
            // Once the rerun starts it may read the changes before a later push's: that push queues a new one.
            // Only this rerun can be pending for the account while it waits, so removing by key is safe.
            lock (_gate) _pendingInboxReruns.Remove(accountId);
        }

        return await RunInboxPassCoreAsync(accountId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RunInboxPassCoreAsync(Guid accountId, CancellationToken token)
    {
        var result = await _synchronizationManager.SynchronizeMailAsync(new MailSynchronizationOptions
        {
            AccountId = accountId,
            Type = MailSynchronizationType.InboxOnly
        }, token).ConfigureAwait(false);

        if (result.CompletedState is not (SynchronizationCompletedState.Success or SynchronizationCompletedState.PartiallyCompleted))
            return false;

        await ClearInvalidCredentialAttentionIfNeededAsync(accountId).ConfigureAwait(false);
        return true;
    }

    private async Task RunSynchronizationPassAsync(CancellationToken cancellationToken)
    {
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var token = source.Token;

        // Each mode runs even when an earlier one fails, as the separate loops would.
        await RunPassStepAsync(ExecuteAutoSynchronizationAsync, "mail", token).ConfigureAwait(false);
        await RunPassStepAsync(ExecuteCalendarAutoSynchronizationAsync, "calendar", token).ConfigureAwait(false);
        await RunPassStepAsync(ct => RunRareModeIfDueAsync(
            () => _preferencesService.TaskLastAutoSynchronizationUtcTicks,
            ticks => _preferencesService.TaskLastAutoSynchronizationUtcTicks = ticks,
            ExecuteTaskAutoSynchronizationAsync, ct), "task", token).ConfigureAwait(false);
        await RunPassStepAsync(ct => RunRareModeIfDueAsync(
            () => _preferencesService.ContactLastAutoSynchronizationUtcTicks,
            ticks => _preferencesService.ContactLastAutoSynchronizationUtcTicks = ticks,
            ExecuteContactAutoSynchronizationAsync, ct), "contact", token).ConfigureAwait(false);
    }

    private static async Task RunPassStepAsync(Func<CancellationToken, Task> step, string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await step(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Information($"Bounded {name} synchronization failed: {ex.Message}");
        }
    }

    private static string GetSynchronizationFailureMessage(
        MailSynchronizationType synchronizationType,
        IEnumerable<SynchronizationIssue> issues,
        string? exceptionMessage)
    {
        var issueMessage = FormatSynchronizationIssues(issues);
        if (!string.IsNullOrWhiteSpace(issueMessage))
        {
            return issueMessage;
        }

        if (!string.IsNullOrWhiteSpace(exceptionMessage))
        {
            return exceptionMessage;
        }

        return synchronizationType switch
        {
            MailSynchronizationType.Alias => Translator.Exception_FailedToSynchronizeAliases,
            MailSynchronizationType.Categories => Translator.Exception_FailedToSynchronizeCategories,
            MailSynchronizationType.UpdateProfile => Translator.Exception_FailedToSynchronizeProfileInformation,
            _ => Translator.Exception_FailedToSynchronizeFolders
        };
    }

    private static string GetCalendarSynchronizationFailureMessage(
        CalendarSynchronizationType synchronizationType,
        IEnumerable<SynchronizationIssue> issues,
        string? exceptionMessage)
    {
        var issueMessage = FormatSynchronizationIssues(issues);
        if (!string.IsNullOrWhiteSpace(issueMessage))
        {
            return issueMessage;
        }

        if (!string.IsNullOrWhiteSpace(exceptionMessage))
        {
            return exceptionMessage;
        }

        return synchronizationType switch
        {
            CalendarSynchronizationType.CalendarMetadata => Translator.Exception_FailedToSynchronizeCalendarMetadata,
            CalendarSynchronizationType.Strict => Translator.Exception_FailedToSynchronizeCalendarData,
            _ => Translator.Exception_FailedToSynchronizeCalendarEvents
        };
    }

    private static string GetSynchronizationFailureMessage(
        IEnumerable<SynchronizationIssue> issues,
        string? exceptionMessage,
        string fallbackMessage)
    {
        var issueMessage = FormatSynchronizationIssues(issues);
        if (!string.IsNullOrWhiteSpace(issueMessage))
            return issueMessage;

        return !string.IsNullOrWhiteSpace(exceptionMessage)
            ? exceptionMessage
            : fallbackMessage;
    }

    private void QueueSynchronizationFailure(string message, InfoBarMessageType severity)
        => _messenger.Send(new InfoBarMessageRequested(severity, Translator.Info_SyncFailedTitle, message));

    private static string? FormatSynchronizationIssues(IEnumerable<SynchronizationIssue> issues)
    {
        if (issues == null)
        {
            return null;
        }

        var issueLines = issues
            .Where(issue => issue != null && !string.IsNullOrWhiteSpace(issue.Message))
            .Select(issue => string.IsNullOrWhiteSpace(issue.ScopeName)
                ? issue.Message
                : string.Format(Translator.SynchronizationIssueFormat_WithScope, issue.ScopeName, issue.Message))
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToList();

        return issueLines.Count == 0 ? null : string.Join(Environment.NewLine, issueLines);
    }

    private async Task RunAutoSynchronizationLoopAsync(TimeSpan interval, Task? startupPass, CancellationToken cancellationToken)
    {
        try
        {
            // The first pass catches up whatever arrived while the app was closed. At launch the ordered
            // startup pass is that first run; a loop restarted later runs its own.
            if (startupPass != null)
                await startupPass.WaitAsync(cancellationToken);
            else
                await ExecuteAutoSynchronizationAsync(cancellationToken);

            using var timer = new PeriodicTimer(interval, _timeProvider);

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                // Each account decides for itself inside, through the coordinator's live coverage.
                await ExecuteAutoSynchronizationAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // no-op
        }
        catch (Exception ex)
        {
            Log.Information($"Automatic sync loop failed: {ex.Message}");
        }
    }

    private async Task RunCalendarAutoSynchronizationLoopAsync(TimeSpan interval, Task? startupPass, CancellationToken cancellationToken)
    {
        try
        {
            if (startupPass != null)
                await startupPass.WaitAsync(cancellationToken);
            else
                await ExecuteCalendarAutoSynchronizationAsync(cancellationToken);

            using var timer = new PeriodicTimer(interval, _timeProvider);

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await ExecuteCalendarAutoSynchronizationAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // no-op
        }
        catch (Exception ex)
        {
            Log.Information($"Automatic calendar sync loop failed: {ex.Message}");
        }
    }

    private async Task RunRareModeLoopAsync(Func<long> getLastSynchronizedTicks, Action<long> setLastSynchronizedTicks,
        Func<CancellationToken, Task> operation, string name, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(RareModeDueCheckInterval, _timeProvider);

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RunRareModeIfDueAsync(getLastSynchronizedTicks, setLastSynchronizedTicks, operation, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // no-op
        }
        catch (Exception ex)
        {
            Log.Information($"Automatic {name} sync loop failed: {ex.Message}");
        }
    }

    private async Task RunRareModeIfDueAsync(Func<long> getLastSynchronizedTicks, Action<long> setLastSynchronizedTicks,
        Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcTicks;
        var last = getLastSynchronizedTicks();

        // No timestamp yet, or one from the future after a clock change: start the window now.
        if (last <= 0 || last > now)
        {
            setLastSynchronizedTicks(now);
            return;
        }

        if (now - last < RareModeSynchronizationInterval.Ticks)
            return;

        // Stamp before running so a failing provider, such as an exhausted quota, waits
        // for the next window instead of retrying on every check.
        setLastSynchronizedTicks(now);
        await operation(cancellationToken);
    }

    private async Task ExecuteTaskAutoSynchronizationAsync(CancellationToken cancellationToken)
    {
        if (_synchronizationManager == null || _accountService == null)
            return;

        var accounts = await _accountService.GetAccountsAsync();
        foreach (var account in accounts.Where(a => a.IsTaskAccessGranted && !a.IsTaskReauthorizationRequired && !a.IsNetworkAccessBlocked()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _synchronizationManager.SynchronizeTasksAsync(new TaskSynchronizationOptions
            {
                AccountId = account.Id,
                Type = TaskSynchronizationType.Delta
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteContactAutoSynchronizationAsync(CancellationToken cancellationToken)
    {
        if (_synchronizationManager == null || _accountService == null)
            return;

        var accounts = await _accountService.GetAccountsAsync();
        foreach (var account in accounts.Where(a => a.IsContactAccessGranted && !a.IsNetworkAccessBlocked()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _synchronizationManager.SynchronizeContactsAsync(new ContactSynchronizationOptions
            {
                AccountId = account.Id,
                Type = ContactSynchronizationType.Delta
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ExecuteAutoSynchronizationAsync(CancellationToken cancellationToken)
    {
        if (_synchronizationManager == null || _accountService == null)
            return;

        bool lockTaken = false;

        try
        {
            lockTaken = await _autoSynchronizationSemaphore.WaitAsync(0, cancellationToken);
            if (!lockTaken)
                return;

            var accounts = await _accountService.GetAccountsAsync();
            var currentAccountIds = accounts.Select(a => a.Id).ToHashSet();
            foreach (var staleAccountId in _lastFullFolderSyncUtc.Keys.Where(a => !currentAccountIds.Contains(a)).ToList())
            {
                _lastFullFolderSyncUtc.TryRemove(staleAccountId, out _);
            }

            var synchronizationTasks = accounts
                .Select(account => ExecuteAutoSynchronizationForAccountAsync(account, cancellationToken))
                .ToList();

            await Task.WhenAll(synchronizationTasks);
        }
        finally
        {
            if (lockTaken)
            {
                _autoSynchronizationSemaphore.Release();
            }
        }
    }

    private async Task ExecuteCalendarAutoSynchronizationAsync(CancellationToken cancellationToken)
    {
        if (_synchronizationManager == null || _accountService == null)
            return;

        await _autoSynchronizationSemaphore.WaitAsync(cancellationToken);

        try
        {
            var accounts = await _accountService.GetAccountsAsync();
            // Accounts that need attention are left alone until the user fixes them; the timer never retries them.
            var synchronizationTasks = accounts
                .Where(account => account.IsCalendarAccessGranted && !account.IsNetworkAccessBlocked())
                .Select(account => ExecuteCalendarAutoSynchronizationForAccountAsync(account, cancellationToken))
                .ToList();

            await Task.WhenAll(synchronizationTasks);
        }
        finally
        {
            _autoSynchronizationSemaphore.Release();
        }
    }

    private async Task ExecuteCalendarAutoSynchronizationForAccountAsync(
        Wino.Core.Domain.Entities.Shared.MailAccount account,
        CancellationToken cancellationToken)
    {
        if (_synchronizationManager == null)
            return;

        cancellationToken.ThrowIfCancellationRequested();

        if (_synchronizationManager.IsAccountSynchronizing(account.Id))
            return;

        await _synchronizationManager.SynchronizeCalendarAsync(new CalendarSynchronizationOptions
        {
            AccountId = account.Id,
            Type = CalendarSynchronizationType.CalendarMetadata
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteAutoSynchronizationForAccountAsync(Wino.Core.Domain.Entities.Shared.MailAccount account, CancellationToken cancellationToken)
    {
        if (_synchronizationManager == null)
            return;

        cancellationToken.ThrowIfCancellationRequested();

        if (_synchronizationManager.IsAccountSynchronizing(account.Id))
            return;

        if (!account.IsMailAccessGranted)
            return;

        // An account that needs attention (rejected credentials, certificate, missing system folders, offline demo)
        // is left alone until the user fixes it. Retrying it on every tick only repeats the failure.
        if (account.IsNetworkAccessBlocked())
            return;

        // A full pass is due once per InboxSyncsPerFullSync intervals, measured in time so that accounts covered
        // live (which skip their inbox ticks) still get it. Covered accounts skip everything else.
        var now = _timeProvider.GetUtcNow();
        var fullPassDue = _lastFullFolderSyncUtc.TryGetValue(account.Id, out var lastFullPass)
            && now - lastFullPass >= _coordinator.MailSyncInterval * InboxSyncsPerFullSync;

        if (!fullPassDue && !_coordinator.ShouldRunTimerSync(account.Id))
            return;

        var options = new MailSynchronizationOptions
        {
            AccountId = account.Id,
            Type = fullPassDue ? MailSynchronizationType.FullFolders : MailSynchronizationType.InboxOnly
        };

        var result = await _synchronizationManager.SynchronizeMailAsync(options, cancellationToken);
        var succeeded = result.CompletedState is SynchronizationCompletedState.Success or SynchronizationCompletedState.PartiallyCompleted;

        // A full pass restarts the clock whether or not it succeeded (the tick counter used to reset the same way),
        // so a failing full pass waits another cycle instead of repeating on every tick.
        if (fullPassDue)
            _lastFullFolderSyncUtc[account.Id] = now;

        if (succeeded)
        {
            await ClearInvalidCredentialAttentionIfNeededAsync(account.Id);

            // The first successful pass starts the clock.
            _lastFullFolderSyncUtc.TryAdd(account.Id, now);
        }

        _coordinator.ReportTimerMailSyncCompleted(account.Id, succeeded, _timeProvider.GetUtcNow());
    }

    private async Task ClearInvalidCredentialAttentionIfNeededAsync(Guid accountId)
    {
        if (_accountService == null)
            return;

        var account = await _accountService.GetAccountAsync(accountId);

        if (account?.AttentionReason != AccountAttentionReason.InvalidCredentials)
            return;

        await _accountService.ClearAccountAttentionAsync(accountId);
    }


}
