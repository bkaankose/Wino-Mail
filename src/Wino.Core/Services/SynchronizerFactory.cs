using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Synchronizers;
using Wino.Services.Interfaces;

namespace Wino.Core.Services;

public class SynchronizerFactory : ISynchronizerFactory
{
    private bool isInitialized = false;

    private readonly IAccountService _accountService;
    private readonly IMailProviderRegistry _providers;

    private readonly ConcurrentDictionary<Guid, IWinoSynchronizerBase> _synchronizers = new();
    internal IReadOnlyDictionary<Guid, IWinoSynchronizerBase> CachedSynchronizers => _synchronizers;

    // User actions that were still queued when an account's synchronizer was torn down (sign-in
    // fix, server settings change). The next synchronizer for the account queues them again.
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<IRequestBase>> _carriedOverRequests = new();
    private readonly ConcurrentDictionary<Guid, byte> _accountsWithRequeuedRequests = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _accountGates = new();

    private SemaphoreSlim GetAccountGate(Guid accountId) => _accountGates.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));

    public SynchronizerFactory(IAccountService accountService, IMailProviderRegistry providers)
    {
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public async Task<IWinoSynchronizerBase> GetAccountSynchronizerAsync(Guid accountId)
    {
        var gate = GetAccountGate(accountId);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var account = await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
            if (account == null)
            {
                // The account was deleted; its queued actions have nothing left to run against. A synchronizer
                // created while the deletion was running would otherwise keep its live listener open.
                _carriedOverRequests.TryRemove(accountId, out _);
                _accountsWithRequeuedRequests.TryRemove(accountId, out _);
                if (_synchronizers.TryRemove(accountId, out var orphaned))
                {
                    try { await orphaned.KillSynchronizerAsync().ConfigureAwait(false); }
                    catch (Exception ex) { Log.Warning(ex, "Failed to stop the synchronizer of removed account {AccountId}.", accountId); }
                }
                return null;
            }
            if (_synchronizers.TryGetValue(accountId, out var existing))
            {
                if (!SynchronizationManager.RequiresSynchronizerRefresh(existing.Account, account) &&
                    CanReuse(existing, account))
                {
                    await ApplyAttentionStateAsync(existing, account).ConfigureAwait(false);
                    return existing;
                }
                // Complete teardown before publishing a replacement for this account.
                try { await existing.KillSynchronizerAsync().ConfigureAwait(false); }
                finally
                {
                    CarryOverQueuedRequests(existing);
                    _synchronizers.TryRemove(accountId, out _);
                }
            }
            return CreateAndCache(account);
        }
        finally { gate.Release(); }
    }

    private IWinoSynchronizerBase CreateIntegratorWithDefaultProcessor(MailAccount mailAccount)
    {
        var module = _providers.Find(mailAccount.ProviderType);
        var synchronizer = module?.CreateSynchronizer(mailAccount);

        if (module != null && synchronizer is IProviderTraitsReceiver receiver)
            receiver.ProviderTraits = module.GetTraits(mailAccount);

        return synchronizer;
    }

    private bool CanReuse(IWinoSynchronizerBase existing, MailAccount account)
        => _providers.Find(account.ProviderType)?.CanReuseSynchronizer(existing, account) ?? true;

    public IWinoSynchronizerBase CreateNewSynchronizer(MailAccount account)
    {
        var gate = GetAccountGate(account.Id);
        // This legacy synchronous entry point must not block the UI on async teardown.
        if (!gate.Wait(0))
            throw new InvalidOperationException("Synchronizer lifecycle is busy. Use GetAccountSynchronizerAsync.");
        try
        {
            return _synchronizers.TryGetValue(account.Id, out var existing) ? existing : CreateAndCache(account);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Desktop IMAP and Exchange never go through the relay, so their own listener (IMAP IDLE, Exchange notifications)
    /// is what makes them instant. It runs in both sync methods, as it did before the relay existed; the timer stays the
    /// safety net. The sync coordinator reads its state to decide which accounts the timer may skip.
    /// </summary>
    private IWinoSynchronizerBase CreateAndCache(MailAccount account)
    {
        var synchronizer = CreateIntegratorWithDefaultProcessor(account);
        _synchronizers[account.Id] = synchronizer;

        if (synchronizer != null && _carriedOverRequests.TryRemove(account.Id, out var carriedOverRequests))
        {
            foreach (var request in carriedOverRequests)
                synchronizer.QueueRequest(request);

            _accountsWithRequeuedRequests[account.Id] = 0;

            Log.Information("Re-queued {RequestCount} request(s) from the previous synchronizer of account {AccountId}.", carriedOverRequests.Count, account.Id);
        }
        if (synchronizer is ILiveChangeListener listener && !account.IsNetworkAccessBlocked())
        {
            _ = listener.StartListeningAsync();
        }
        return synchronizer;
    }

    /// <summary>
    /// Copies the persisted attention state into the cached synchronizer. Its transports read
    /// that state before every connection, so this is what takes an account offline or back online.
    /// </summary>
    public async Task ApplyAccountAttentionAsync(MailAccount account)
    {
        if (account == null) return;

        var gate = GetAccountGate(account.Id);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_synchronizers.TryGetValue(account.Id, out var existing))
                await ApplyAttentionStateAsync(existing, account).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task ApplyAttentionStateAsync(IWinoSynchronizerBase synchronizer, MailAccount current)
    {
        var cached = synchronizer.Account;
        if (cached == null || cached.AttentionReason == current.AttentionReason) return;

        var wasBlocked = cached.IsNetworkAccessBlocked();
        cached.AttentionReason = current.AttentionReason;
        var isBlocked = cached.IsNetworkAccessBlocked();

        if (wasBlocked == isBlocked) return;

        if (synchronizer is not ILiveChangeListener listener) return;

        if (isBlocked)
        {
            await listener.SuspendListeningAsync().ConfigureAwait(false);
        }
        else
        {
            Log.Information("Account {AccountId} no longer needs attention. Resuming its live change listener.", cached.Id);
            await listener.StartListeningAsync().ConfigureAwait(false);
        }
    }

    public async Task InitializeAsync()
    {
        if (isInitialized) return;

        var accounts = await _accountService.GetAccountsAsync();

        foreach (var account in accounts)
        {
            CreateNewSynchronizer(account);
        }

        isInitialized = true;
    }

    public async Task DeleteSynchronizerAsync(Guid accountId)
    {
        var gate = GetAccountGate(accountId);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_synchronizers.TryGetValue(accountId, out var synchronizer))
            {
                try { await synchronizer.KillSynchronizerAsync().ConfigureAwait(false); }
                finally
                {
                    CarryOverQueuedRequests(synchronizer);
                    _synchronizers.TryRemove(accountId, out _);
                }
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Keeps the requests a synchronizer had not executed yet so the account's next synchronizer
    /// runs them. Runs after the old synchronizer was stopped, so it no longer executes them itself.
    /// </summary>
    private void CarryOverQueuedRequests(IWinoSynchronizerBase synchronizer)
    {
        var accountId = synchronizer.Account?.Id;
        if (accountId is not { } id)
            return;

        var queuedRequests = synchronizer.TakeQueuedRequests();
        if (queuedRequests.Count == 0)
            return;

        _carriedOverRequests.AddOrUpdate(
            id,
            queuedRequests,
            (_, existing) => existing.Concat(queuedRequests).ToList());
    }

    /// <summary>
    /// Whether a new synchronizer for the account received requests from its predecessor since
    /// the last call. Those requests still need an execution run.
    /// </summary>
    internal bool TryConsumeRequeuedRequests(Guid accountId) => _accountsWithRequeuedRequests.TryRemove(accountId, out _);
}
