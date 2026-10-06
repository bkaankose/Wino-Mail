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
using Wino.Core.Domain.Interfaces;
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
    private readonly object _gate = new();
    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IPreferencesService _preferencesService;
    private readonly IAccountService _accountService;
    private readonly IMessenger _messenger;
    private readonly IWinoLogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<CancellationToken, Task> _initializeServices;
    private readonly Func<CancellationToken, Task> _backgroundStartup;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _autoSynchronizationSemaphore = new(1, 1);
    private readonly ConcurrentDictionary<Guid, int> _inboxSyncCounters = new();
    private readonly ConcurrentDictionary<int, Task> _jobs = new();
    private readonly Queue<ApplicationRuntimeState> _stateChanges = new();
    private readonly HashSet<Guid> _pendingCreatedAccounts = new();
    private bool _dispatchingStateChanges;
    private int _jobId;
    private Task? _initialization;
    private Task? _stopping;
    private CancellationTokenSource? _mailLoop;
    private CancellationTokenSource? _calendarLoop;
    private bool _hasAccounts;
    private bool _subscribed;
    private ApplicationRuntimeState _state;

    public ApplicationRuntime(ISynchronizationManager synchronizationManager, IAccountService accountService,
        IPreferencesService preferencesService, IMessenger messenger, IWinoLogger logger,
        Func<CancellationToken, Task> initializeServices, Func<CancellationToken, Task> backgroundStartup,
        TimeProvider? timeProvider = null)
    {
        _synchronizationManager = synchronizationManager;
        _accountService = accountService;
        _preferencesService = preferencesService;
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
            var cancellation = _synchronizationManager.GetAllSynchronizers()
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
        _subscribed = true;
    }

    private void UnregisterRecipients()
    {
        _messenger.UnregisterAll(this);
        if (_subscribed) _preferencesService.PreferenceChanged -= PreferencesChanged;
        _subscribed = false;
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
        lock (_gate)
        {
            _hasAccounts = accounts.Any();
            var ids = accounts.Select(x => x.Id).ToHashSet();
            foreach (var id in _inboxSyncCounters.Keys.Where(x => !ids.Contains(x)))
                _inboxSyncCounters.TryRemove(id, out _);

            if (!_hasAccounts) StopLoops();
        }
    }

    private void PreferencesChanged(object? sender, string propertyName)
    {
        lock (_gate)
        {
            if (_state != ApplicationRuntimeState.Running || !_hasAccounts) return;
            if (propertyName == nameof(IPreferencesService.EmailSyncIntervalMinutes)) RestartMailLoop();
            if (propertyName == nameof(IPreferencesService.CalendarSyncIntervalMinutes)) RestartCalendarLoop();
        }
    }

    private void EnsureLoops()
    {
        if (_state != ApplicationRuntimeState.Running || !_hasAccounts || _lifetime.IsCancellationRequested) return;
        if (_mailLoop == null) RestartMailLoop();
        if (_calendarLoop == null) RestartCalendarLoop();
    }

    private void RestartMailLoop()
    {
        _mailLoop?.Cancel();
        var source = _mailLoop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Track(() => RunLoopWithCleanupAsync(source, () => RunAutoSynchronizationLoopAsync(
            TimeSpan.FromMinutes(Math.Max(1, _preferencesService.EmailSyncIntervalMinutes)), source.Token)));
    }

    private void RestartCalendarLoop()
    {
        _calendarLoop?.Cancel();
        var source = _calendarLoop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Track(() => RunLoopWithCleanupAsync(source, () => RunCalendarAutoSynchronizationLoopAsync(
            TimeSpan.FromMinutes(Math.Max(1, _preferencesService.CalendarSyncIntervalMinutes)), source.Token)));
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
            }
            source.Dispose();
        }
    }

    private void StopLoops()
    {
        _mailLoop?.Cancel();
        _calendarLoop?.Cancel();
        _mailLoop = null;
        _calendarLoop = null;
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

    private async Task RunAutoSynchronizationLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAutoSynchronizationAsync(cancellationToken);

            using var timer = new PeriodicTimer(interval, _timeProvider);

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
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

    private async Task RunCalendarAutoSynchronizationLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
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
            foreach (var staleAccountId in _inboxSyncCounters.Keys.Where(a => !currentAccountIds.Contains(a)).ToList())
            {
                _inboxSyncCounters.TryRemove(staleAccountId, out _);
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
            var synchronizationTasks = accounts
                .Where(account => account.IsCalendarAccessGranted)
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

        if (account.IsContactAccessGranted)
        {
            await _synchronizationManager.SynchronizeContactsAsync(new ContactSynchronizationOptions
            {
                AccountId = account.Id,
                Type = ContactSynchronizationType.Delta
            }, cancellationToken).ConfigureAwait(false);
        }

        if (account.IsTaskAccessGranted && !account.IsTaskReauthorizationRequired)
        {
            await _synchronizationManager.SynchronizeTasksAsync(new TaskSynchronizationOptions
            {
                AccountId = account.Id,
                Type = TaskSynchronizationType.Delta
            }, cancellationToken).ConfigureAwait(false);
        }

        if (!account.IsMailAccessGranted)
            return;

        var inboxSyncOptions = new MailSynchronizationOptions
        {
            AccountId = account.Id,
            Type = MailSynchronizationType.InboxOnly
        };

        var inboxSyncResult = await _synchronizationManager.SynchronizeMailAsync(inboxSyncOptions, cancellationToken);

        if (inboxSyncResult.CompletedState is SynchronizationCompletedState.Success or SynchronizationCompletedState.PartiallyCompleted)
        {
            await ClearInvalidCredentialAttentionIfNeededAsync(account.Id);

            var inboxSyncCount = _inboxSyncCounters.AddOrUpdate(account.Id, 1, (_, currentCount) => currentCount + 1);

            if (inboxSyncCount >= InboxSyncsPerFullSync)
            {
                var fullSyncOptions = new MailSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = MailSynchronizationType.FullFolders
                };

                await _synchronizationManager.SynchronizeMailAsync(fullSyncOptions, cancellationToken);
                _inboxSyncCounters[account.Id] = 0;
            }
        }

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
