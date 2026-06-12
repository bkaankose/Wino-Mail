using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.BackgroundService.Services;

/// <summary>
/// Periodic automatic synchronization loop. Port of the loop that used to live in
/// Wino.Mail.WinUI App.xaml.cs: inbox sync each tick, full-folder sync every fourth
/// successful inbox sync, calendar metadata sync for accounts with calendar access,
/// plus clearing stale invalid-credential attention flags.
/// </summary>
public sealed class SynchronizationLoopService : IDisposable
{
    private const int InboxSyncsPerFullSync = 4;

    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IAccountService _accountService;
    private readonly IPreferencesService _preferencesService;
    private readonly ILogger _logger = Log.ForContext<SynchronizationLoopService>();
    private readonly SemaphoreSlim _runLock = new(1);
    private readonly ConcurrentDictionary<Guid, int> _inboxSyncCounters = new();

    private CancellationTokenSource? _loopCts;

    public SynchronizationLoopService(ISynchronizationManager synchronizationManager,
                                      IAccountService accountService,
                                      IPreferencesService preferencesService)
    {
        _synchronizationManager = synchronizationManager;
        _accountService = accountService;
        _preferencesService = preferencesService;

        _preferencesService.PreferenceChanged += OnPreferenceChanged;
    }

    public void Start() => RestartLoop();

    public void Stop()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = null;
    }

    private void OnPreferenceChanged(object? sender, string propertyName)
    {
        if (propertyName == nameof(IPreferencesService.EmailSyncIntervalMinutes))
        {
            RestartLoop();
        }
    }

    private void RestartLoop()
    {
        Stop();

        var intervalMinutes = Math.Max(1, _preferencesService.EmailSyncIntervalMinutes);
        _loopCts = new CancellationTokenSource();

        _ = RunLoopAsync(TimeSpan.FromMinutes(intervalMinutes), _loopCts.Token);
        _logger.Information("Automatic sync loop started. Interval: {IntervalMinutes} minute(s).", intervalMinutes);
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAutoSynchronizationAsync(cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(interval);

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await ExecuteAutoSynchronizationAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // no-op
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Automatic sync loop failed.");
        }
    }

    private async Task ExecuteAutoSynchronizationAsync(CancellationToken cancellationToken)
    {
        bool lockTaken = false;

        try
        {
            lockTaken = await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!lockTaken)
                return;

            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

            var currentAccountIds = accounts.Select(a => a.Id).ToHashSet();
            foreach (var staleAccountId in _inboxSyncCounters.Keys.Where(a => !currentAccountIds.Contains(a)).ToList())
            {
                _inboxSyncCounters.TryRemove(staleAccountId, out _);
            }

            var synchronizationTasks = accounts
                .Select(account => ExecuteAutoSynchronizationForAccountAsync(account, cancellationToken))
                .ToList();

            await Task.WhenAll(synchronizationTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Automatic synchronization tick failed.");
        }
        finally
        {
            if (lockTaken)
            {
                _runLock.Release();
            }
        }
    }

    private async Task ExecuteAutoSynchronizationForAccountAsync(Wino.Core.Domain.Entities.Shared.MailAccount account, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_synchronizationManager.IsAccountSynchronizing(account.Id))
            return;

        var inboxSyncOptions = new MailSynchronizationOptions
        {
            AccountId = account.Id,
            Type = MailSynchronizationType.InboxOnly
        };

        var inboxSyncResult = await _synchronizationManager.SynchronizeMailAsync(inboxSyncOptions, cancellationToken).ConfigureAwait(false);

        if (inboxSyncResult.CompletedState is SynchronizationCompletedState.Success or SynchronizationCompletedState.PartiallyCompleted)
        {
            await ClearInvalidCredentialAttentionIfNeededAsync(account.Id).ConfigureAwait(false);

            var inboxSyncCount = _inboxSyncCounters.AddOrUpdate(account.Id, 1, (_, currentCount) => currentCount + 1);

            if (inboxSyncCount >= InboxSyncsPerFullSync)
            {
                var fullSyncOptions = new MailSynchronizationOptions
                {
                    AccountId = account.Id,
                    Type = MailSynchronizationType.FullFolders
                };

                await _synchronizationManager.SynchronizeMailAsync(fullSyncOptions, cancellationToken).ConfigureAwait(false);
                _inboxSyncCounters[account.Id] = 0;
            }
        }

        if (!account.IsCalendarAccessGranted)
            return;

        var calendarOptions = new CalendarSynchronizationOptions
        {
            AccountId = account.Id,
            Type = CalendarSynchronizationType.CalendarMetadata
        };

        await _synchronizationManager.SynchronizeCalendarAsync(calendarOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearInvalidCredentialAttentionIfNeededAsync(Guid accountId)
    {
        var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);

        if (account?.AttentionReason != AccountAttentionReason.InvalidCredentials)
            return;

        await _accountService.ClearAccountAttentionAsync(accountId).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _preferencesService.PreferenceChanged -= OnPreferenceChanged;
        Stop();
        _runLock.Dispose();
    }
}
