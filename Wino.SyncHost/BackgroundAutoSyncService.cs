using System.Collections.Concurrent;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.SyncHost;

internal sealed class BackgroundAutoSyncService
{
    private const int InboxSyncsPerFullSync = 20;

    private readonly ISynchronizationManager _synchronizationManager;
    private readonly IPreferencesService _preferencesService;
    private readonly IAccountService _accountService;
    private readonly SemaphoreSlim _synchronizationSemaphore = new(1, 1);
    private readonly ConcurrentDictionary<Guid, int> _inboxSyncCounters = [];
    private readonly ILogger _logger = Log.ForContext<BackgroundAutoSyncService>();

    public BackgroundAutoSyncService(
        ISynchronizationManager synchronizationManager,
        IPreferencesService preferencesService,
        IAccountService accountService)
    {
        _synchronizationManager = synchronizationManager;
        _preferencesService = preferencesService;
        _accountService = accountService;
    }

    public void Start(CancellationToken cancellationToken)
        => _ = RunAsync(cancellationToken);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, _preferencesService.EmailSyncIntervalMinutes)));

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Background auto synchronization loop failed.");
        }
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        bool lockTaken = false;

        try
        {
            lockTaken = await _synchronizationSemaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!lockTaken)
                return;

            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            var currentAccountIds = accounts.Select(account => account.Id).ToHashSet();

            foreach (var staleAccountId in _inboxSyncCounters.Keys.Where(id => !currentAccountIds.Contains(id)).ToList())
            {
                _inboxSyncCounters.TryRemove(staleAccountId, out _);
            }

            await Task.WhenAll(accounts.Select(account => ExecuteForAccountAsync(account, cancellationToken))).ConfigureAwait(false);
        }
        finally
        {
            if (lockTaken)
            {
                _synchronizationSemaphore.Release();
            }
        }
    }

    private async Task ExecuteForAccountAsync(MailAccount account, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_synchronizationManager.IsAccountSynchronizing(account.Id))
            return;

        var inboxSyncResult = await _synchronizationManager.SynchronizeMailAsync(
            new MailSynchronizationOptions
            {
                AccountId = account.Id,
                Type = MailSynchronizationType.InboxOnly
            },
            cancellationToken).ConfigureAwait(false);

        if (inboxSyncResult.CompletedState is SynchronizationCompletedState.Success or SynchronizationCompletedState.PartiallyCompleted)
        {
            await ClearInvalidCredentialAttentionIfNeededAsync(account.Id).ConfigureAwait(false);

            var inboxSyncCount = _inboxSyncCounters.AddOrUpdate(account.Id, 1, (_, currentCount) => currentCount + 1);

            if (inboxSyncCount >= InboxSyncsPerFullSync)
            {
                await _synchronizationManager.SynchronizeMailAsync(
                    new MailSynchronizationOptions
                    {
                        AccountId = account.Id,
                        Type = MailSynchronizationType.FullFolders
                    },
                    cancellationToken).ConfigureAwait(false);

                _inboxSyncCounters[account.Id] = 0;
            }
        }

        if (!account.IsCalendarAccessGranted)
            return;

        await _synchronizationManager.SynchronizeCalendarAsync(
            new CalendarSynchronizationOptions
            {
                AccountId = account.Id,
                Type = CalendarSynchronizationType.CalendarMetadata
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearInvalidCredentialAttentionIfNeededAsync(Guid accountId)
    {
        var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);

        if (account?.AttentionReason == AccountAttentionReason.InvalidCredentials)
        {
            await _accountService.ClearAccountAttentionAsync(accountId).ConfigureAwait(false);
        }
    }
}
