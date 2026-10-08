using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Services;

public sealed class DraftUpdateCoordinator : IDraftUpdateCoordinator, IAsyncDisposable
{
    private sealed class Work
    {
        public DraftUpdateSnapshot Pending;
        public CancellationTokenSource Cancellation;
        public Task Runner = Task.CompletedTask;
        public bool Stopped;
        public long MappingVersion;
    }

    private readonly object _gate = new();
    private readonly Dictionary<(Guid Account, Guid Draft), Work> _work = new();
    private readonly ISynchronizationManager _manager;
    private readonly IMailService _mailService;
    private readonly IWinoLogger _logger;
    private readonly DraftUpdateRegistry _registry;
    private bool _disposed;

    public DraftUpdateCoordinator(ISynchronizationManager manager, IMailService mailService,
        IWinoLogger logger, DraftUpdateRegistry registry)
    {
        _manager = manager;
        _mailService = mailService;
        _logger = logger;
        _registry = registry;
        _registry.Mapped += OnMapped;
    }

    public void Schedule(DraftUpdateSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var key = (snapshot.AccountId, snapshot.UniqueId);
            if (!_work.TryGetValue(key, out var work)) _work[key] = work = new();
            if (work.Stopped && !work.Runner.IsCompleted) return;
            work.Stopped = false;

            work.Pending = snapshot;
            work.Cancellation?.Cancel();
            Start(key, work);
        }
    }

    private void OnMapped(Guid account, Guid draft)
    {
        lock (_gate)
        {
            if (!_disposed && _work.TryGetValue((account, draft), out var work))
            {
                work.MappingVersion++;
                Start((account, draft), work);
            }
        }
    }

    private void Start((Guid Account, Guid Draft) key, Work work)
    {
        if (!work.Stopped && work.Pending != null && work.Runner.IsCompleted)
            work.Runner = Task.Run(() => RunAsync(key, work));
    }

    private async Task RunAsync((Guid Account, Guid Draft) key, Work work)
    {
        while (true)
        {
            DraftUpdateSnapshot snapshot;
            CancellationToken token;
            long mappingVersion;
            lock (_gate)
            {
                if (work.Stopped || work.Pending == null)
                {
                    work.Runner = Task.CompletedTask;
                    return;
                }
                mappingVersion = work.MappingVersion;
                snapshot = work.Pending;
                work.Pending = null;
                work.Cancellation = new CancellationTokenSource();
                token = work.Cancellation.Token;
            }

            var waitingForMapping = false;
            var succeeded = false;
            try
            {
                var mail = await _mailService.GetSingleMailItemAsync(key.Draft).ConfigureAwait(false);
                if (mail?.AssignedAccount?.Id != key.Account || !mail.IsDraft || mail.AssignedAccount.ProviderType == MailProviderType.POP3)
                {
                    lock (_gate) { work.Stopped = true; work.Pending = null; }
                }
                else if (mail.IsLocalDraft)
                {
                    lock (_gate) work.Pending ??= snapshot;
                    waitingForMapping = true;
                }
                else
                {
                    token.ThrowIfCancellationRequested();
                    var identity = await _manager.UpdateDraftAsync(snapshot, token).ConfigureAwait(false);
                    // A confirmed server write remains real even if a newer save canceled its token.
                    if (identity != null)
                        await _mailService.UpdateDraftIdentityAsync(key.Account, key.Draft, identity).ConfigureAwait(false);
                    succeeded = identity != null;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception)
            {
                _logger.LogDraftUpdateFailure(key.Account, key.Draft);
            }
            finally
            {
                lock (_gate)
                {
                    work.Cancellation?.Dispose();
                    work.Cancellation = null;
                }
            }

            lock (_gate)
            {
                if (succeeded && work.Pending == null && !work.Stopped)
                    _registry.Complete(key.Account, key.Draft, snapshot.Revision);
                if (waitingForMapping && work.MappingVersion == mappingVersion)
                {
                    work.Runner = Task.CompletedTask;
                    return;
                }
            }
        }
    }

    public async Task<MailCopy> StopAsync(Guid accountId, Guid uniqueId)
    {
        var saveLock = _registry.Get(accountId, uniqueId).SaveLock;
        await saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Task runner;
            lock (_gate)
            {
                if (!_work.TryGetValue((accountId, uniqueId), out var work))
                    _work[(accountId, uniqueId)] = work = new();
                work.Stopped = true;
                work.Pending = null;
                work.Cancellation?.Cancel();
                runner = work.Runner;
            }
    
            await runner.ConfigureAwait(false);
            _registry.Release(accountId, uniqueId);
            var mail = await _mailService.GetSingleMailItemAsync(uniqueId).ConfigureAwait(false);
            return mail?.AssignedAccount?.Id == accountId ? mail : null;
        }
        finally { saveLock.Release(); }
    }

    public async Task StopAccountAsync(Guid accountId)
    {
        Guid[] drafts;
        lock (_gate) drafts = _work.Keys.Where(x => x.Account == accountId).Select(x => x.Draft).ToArray();
        await Task.WhenAll(drafts.Select(x => StopAsync(accountId, x))).ConfigureAwait(false);
        lock (_gate)
            foreach (var draft in drafts) _work.Remove((accountId, draft));
        _registry.RemoveAccount(accountId);
    }

    public async ValueTask DisposeAsync()
    {
        Guid[] accounts;
        lock (_gate)
        {
            _disposed = true;
            accounts = _work.Keys.Select(x => x.Account).Distinct().ToArray();
        }
        _registry.Mapped -= OnMapped;
        await Task.WhenAll(accounts.Select(StopAccountAsync)).ConfigureAwait(false);
    }
}
