#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.ContentProcessing;
using Wino.Mail.AI.Cryptography;
using Wino.Mail.Contracts.Intelligence;
using Wino.Mail.Contracts.SemanticIndex;
using Wino.Messaging.UI;

namespace Wino.Services;

public sealed class SemanticIndexCoordinator(
    IDatabaseService databaseService,
    IAccountService accountService,
    IWinoAccountApiClient apiClient,
    ISynchronizationManager synchronizationManager,
    ILocalIntelligenceStore localStore,
    ILocalIntelligenceService localIntelligenceService,
    IContentEnvelopeEncryptor envelopeEncryptor,
    ISemanticIndexJobRegistry jobRegistry,
    ITranslationService translationService,
    IMimeFileService mimeFileService,
    IIntelligenceMessageContextResolver messageResolver,
    IIntelligenceBackend intelligenceBackend) : ISemanticIndexCoordinator, IAsyncDisposable
{
    private const int DownloadPageSize = 100;
    private const int UploadBatchSize = 20;
    private const int DocumentPreparationConcurrency = 8;
    private const int UploadConcurrency = 4;
    private readonly ConcurrentDictionary<Guid, SemanticIndexJobSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<(Guid AccountId, string MessageId), SemanticMessageIndexState> _messageStates = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _automaticQueues = new();
    private readonly ConcurrentDictionary<Guid, byte> _headlineTranslations = new();
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        await _initializeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            await localStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
            WeakReferenceMessenger.Default.Register<AccountSynchronizationCompleted>(this, static (recipient, message) =>
                _ = ((SemanticIndexCoordinator)recipient).HandleSynchronizationCompletedAsync(message));
            _initialized = true;

            var accounts = (await accountService.GetAccountsAsync().ConfigureAwait(false)).ToDictionary(x => x.Id);
            var intents = await localStore.GetJobIntentsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var intent in intents.Where(x => x.BackfillStatus == "in-progress" && accounts.ContainsKey(x.LocalAccountId)))
            {
                try
                {
                    var plan = await CalculatePlanCoreAsync(
                        accounts[intent.LocalAccountId],
                        intent.RangePreset,
                        intent.CutoffUtc,
                        intent.ThroughUtcExclusive,
                        intent.AutomaticallyIndexNewMessages,
                        cancellationToken).ConfigureAwait(false);
                    StartWorker(intent.LocalAccountId, plan, notifyWhenCompleted: false);
                }
                catch
                {
                    SetSnapshot(new(intent.LocalAccountId, SemanticIndexJobStatus.Failed, 0, 0));
                }
            }
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task<SemanticIndexPlan> CalculatePlanAsync(
        Guid localMailAccountId,
        SemanticIndexRangePreset preset,
        bool automaticallyIndexNewMessages,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_headlineTranslations.ContainsKey(localMailAccountId))
            throw new InvalidOperationException("Headline translation is already in progress for this mailbox.");
        SetSnapshot(new(localMailAccountId, SemanticIndexJobStatus.Calculating, 0, 0));
        try
        {
            var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
            var cutoff = preset.CreateCutoff(DateTimeOffset.UtcNow);
            return await CalculatePlanCoreAsync(account, preset, cutoff, null, automaticallyIndexNewMessages, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!jobRegistry.IsRunning(localMailAccountId))
                SetSnapshot(new(localMailAccountId, SemanticIndexJobStatus.Idle, 0, 0));
        }
    }

    public async Task<SemanticIndexPlan> CalculatePlanAsync(
        Guid localMailAccountId,
        DateTimeOffset cutoffUtc,
        bool automaticallyIndexNewMessages,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        SetSnapshot(new(localMailAccountId, SemanticIndexJobStatus.Calculating, 0, 0));
        try
        {
            var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
            return await CalculatePlanCoreAsync(
                account,
                SemanticIndexRangePreset.Custom,
                cutoffUtc,
                null,
                automaticallyIndexNewMessages,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!jobRegistry.IsRunning(localMailAccountId))
                SetSnapshot(new(localMailAccountId, SemanticIndexJobStatus.Idle, 0, 0));
        }
    }

    public async Task<SemanticIndexPlan> CalculatePlanAsync(
        Guid localMailAccountId,
        DateTimeOffset cutoffUtc,
        DateTimeOffset throughUtcExclusive,
        bool automaticallyIndexNewMessages,
        CancellationToken cancellationToken = default)
    {
        if (throughUtcExclusive <= cutoffUtc)
            throw new ArgumentOutOfRangeException(nameof(throughUtcExclusive), "The range end must be after the range start.");

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        SetSnapshot(new(localMailAccountId, SemanticIndexJobStatus.Calculating, 0, 0));
        try
        {
            var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
            return await CalculatePlanCoreAsync(
                account,
                SemanticIndexRangePreset.Custom,
                cutoffUtc,
                throughUtcExclusive,
                automaticallyIndexNewMessages,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!jobRegistry.IsRunning(localMailAccountId))
                SetSnapshot(new(localMailAccountId, SemanticIndexJobStatus.Idle, 0, 0));
        }
    }

    public async Task<SemanticIndexAvailableRange?> GetAvailableRangeAsync(
        Guid localMailAccountId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await messageResolver.GetAvailableRangeAsync(localMailAccountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartIndexingAsync(
        Guid localMailAccountId,
        SemanticIndexPlan plan,
        CancellationToken cancellationToken = default,
        bool notifyWhenCompleted = false)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_headlineTranslations.ContainsKey(localMailAccountId))
            throw new InvalidOperationException("Headline translation is already in progress for this mailbox.");
        if (plan.LocalAccountId != localMailAccountId)
            throw new ArgumentException("The indexing plan belongs to another account.", nameof(plan));
        var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
        if (!account.Preferences.IsSemanticIndexingEnabled)
            throw new InvalidOperationException("Mail intelligence is not enabled for this account.");
        var mailbox = await RequireMailboxAsync(account, cancellationToken).ConfigureAwait(false);
        await SaveProfileAsync(mailbox.MailboxId, plan, "in-progress", cancellationToken).ConfigureAwait(false);
        StartWorker(localMailAccountId, plan, notifyWhenCompleted);
    }

    public async Task<HeadlineTranslationResultDto> TranslateHeadlinesAsync(
        Guid localMailAccountId,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (jobRegistry.IsRunning(localMailAccountId) || !_headlineTranslations.TryAdd(localMailAccountId, 0))
            throw new InvalidOperationException("Indexing and headline translation cannot run at the same time.");
        try
        {
            var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
            var mailbox = await RequireMailboxAsync(account, cancellationToken).ConfigureAwait(false);
            await localStore.SetHeadlineLanguageAsync(account.Id, mailbox.MailboxId, targetLanguage, cancellationToken).ConfigureAwait(false);
            SetSnapshot(new(localMailAccountId, SemanticIndexJobStatus.TranslatingHeadlines, 0, 0));
            var result = await apiClient.TranslateBriefingHeadlinesAsync(mailbox.MailboxId, targetLanguage, cancellationToken).ConfigureAwait(false);
            await localStore.ApplyBriefingHeadlineUpdatesAsync(
                account.Id, mailbox.MailboxId, result.HeadlineLanguage, result.Headlines, result.ThroughArtifactRevision, cancellationToken).ConfigureAwait(false);
            SetSnapshot(new(localMailAccountId, SemanticIndexJobStatus.Completed, result.TranslatedCount, result.TranslatedCount + result.FailedCount));
            return result;
        }
        catch
        {
            SetSnapshot(new(localMailAccountId, SemanticIndexJobStatus.Failed, 0, 0));
            throw;
        }
        finally
        {
            _headlineTranslations.TryRemove(localMailAccountId, out _);
        }
    }

    public async Task CancelIndexingAsync(Guid localMailAccountId)
    {
        if (_automaticQueues.TryGetValue(localMailAccountId, out var queue))
            queue.Clear();

        await jobRegistry.CancelAndWaitAsync(localMailAccountId).ConfigureAwait(false);
        var current = GetJobSnapshot(localMailAccountId);
        SetSnapshot(current with { Status = SemanticIndexJobStatus.Cancelled });
    }

    public async Task IndexMessageAsync(Guid localMailAccountId, string mailUniqueId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_headlineTranslations.ContainsKey(localMailAccountId))
            throw new InvalidOperationException("Headline translation is in progress for this mailbox.");
        var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
        var candidate = (await messageResolver.GetCandidatesAsync(account.Id, null, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(x => string.Equals(x.RemoteMessageId, mailUniqueId, StringComparison.Ordinal) ||
                                  string.Equals(x.ProviderMessageId, mailUniqueId, StringComparison.Ordinal));
        if (candidate is null)
            throw new InvalidOperationException("This message cannot be indexed.");

        var mailbox = await RequireMailboxAsync(account, cancellationToken).ConfigureAwait(false);
        var messageStateKey = (account.Id, candidate.RemoteMessageId);
        _messageStates[messageStateKey] = SemanticMessageIndexState.Queued;
        try
        {
            _messageStates[messageStateKey] = SemanticMessageIndexState.Indexing;
            var winoAccount = await databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("A Wino account is required for mail intelligence.");
            var result = await ProcessBatchAsync(
                account,
                winoAccount.Id,
                mailbox.MailboxId,
                [candidate],
                static _ => { },
                static _ => { },
                cancellationToken).ConfigureAwait(false);
            if (!result.IndexedRemoteMessageIds.Contains(candidate.RemoteMessageId))
                throw new InvalidOperationException("The message could not be indexed.");

            _messageStates[messageStateKey] = SemanticMessageIndexState.Indexed;
        }
        catch
        {
            _messageStates[messageStateKey] = SemanticMessageIndexState.Failed;
            throw;
        }
    }

    public SemanticIndexJobSnapshot GetJobSnapshot(Guid localMailAccountId)
        => _snapshots.TryGetValue(localMailAccountId, out var snapshot)
            ? snapshot
            : new SemanticIndexJobSnapshot(localMailAccountId, SemanticIndexJobStatus.Idle, 0, 0);

    public async Task<SemanticMessageIndexState> GetMessageStateAsync(Guid localMailAccountId, string mailUniqueId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var account = await accountService.GetAccountAsync(localMailAccountId).ConfigureAwait(false);
        if (account is null || !account.Preferences.IsSemanticIndexingEnabled)
            return SemanticMessageIndexState.Unsupported;
        var candidate = (await messageResolver.GetCandidatesAsync(account.Id, null, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(x => string.Equals(x.RemoteMessageId, mailUniqueId, StringComparison.Ordinal) ||
                                  string.Equals(x.ProviderMessageId, mailUniqueId, StringComparison.Ordinal));
        if (candidate is null)
            return SemanticMessageIndexState.Unsupported;
        var artifacts = await localStore.GetCurrentArtifactsAsync(
            localMailAccountId, candidate.RemoteMessageId, cancellationToken).ConfigureAwait(false);
        // Persisted artifacts are authoritative. A stale in-memory queue/indexing marker must not
        // make an already processed message look unfinished after metadata has been imported.
        if (artifacts.Any(static artifact => !artifact.IsDeleted))
            return SemanticMessageIndexState.Indexed;

        return _messageStates.TryGetValue((localMailAccountId, candidate.RemoteMessageId), out var state)
            ? state
            : SemanticMessageIndexState.NotIndexed;
    }

    public async Task<SemanticIndexAccountState> GetStateAsync(Guid localMailAccountId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await GetStateCoreAsync(localMailAccountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureMailboxAsync(Guid localMailAccountId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
        await apiClient.EnsureSemanticMailboxAsync(account.Address, (int)account.ProviderType, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SemanticIndexAccountState> DownloadAvailableIntelligenceAsync(
        Guid localMailAccountId,
        IProgress<SemanticIndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
        var mailbox = await RequireMailboxAsync(account, cancellationToken).ConfigureAwait(false);
        var revision = await localStore.GetLastImportedRevisionAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var cursor = revision == 0 ? null : Convert.ToBase64String(BitConverter.GetBytes(revision));
        var downloaded = 0;
        while (true)
        {
            var page = await apiClient.GetIntelligenceArtifactsAsync(
                mailbox.MailboxId, cursor, DownloadPageSize, cancellationToken).ConfigureAwait(false);
            if (page.Items.Count > 0)
            {
                revision = page.Items.Max(artifact => artifact.ArtifactRevision);
                await localStore.ImportAsync(account.Id, mailbox.MailboxId, page.Items, revision, cancellationToken).ConfigureAwait(false);
                downloaded += page.Items.Count;
                progress?.Report(new(downloaded, downloaded));
            }
            if (page.NextCursor is null)
                break;
            cursor = page.NextCursor;
        }
        return await GetStateCoreAsync(localMailAccountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteIndexAsync(Guid localMailAccountId, CancellationToken cancellationToken = default)
    {
        await jobRegistry.CancelAndWaitAsync(localMailAccountId).ConfigureAwait(false);
        var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
        try
        {
            var mailbox = await FindMailboxAsync(account, cancellationToken).ConfigureAwait(false);
            if (mailbox is not null)
            {
                await apiClient.DeleteIntelligenceAsync(mailbox.MailboxId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Local deletion is unconditional. A mailbox that was never indexed is a successful
            // no-op, and revoked consent must never prevent local privacy cleanup.
            await localStore.DeleteMailboxAsync(account.Id, cancellationToken).ConfigureAwait(false);
            _automaticQueues.TryRemove(localMailAccountId, out _);
            SetSnapshot(new(localMailAccountId, SemanticIndexJobStatus.Idle, 0, 0));
        }
    }

    public async Task DeleteLocalIndexAsync(Guid localMailAccountId, CancellationToken cancellationToken = default)
    {
        await jobRegistry.CancelAndWaitAsync(localMailAccountId).ConfigureAwait(false);
        await localStore.DeleteMailboxAsync(localMailAccountId, cancellationToken).ConfigureAwait(false);
        _automaticQueues.TryRemove(localMailAccountId, out _);
        SetSnapshot(new(localMailAccountId, SemanticIndexJobStatus.Idle, 0, 0));
    }

    public async Task ResetLocalStateAsync(CancellationToken cancellationToken = default)
    {
        var localAccountIds = (await accountService.GetAccountsAsync().ConfigureAwait(false))
            .Select(static account => account.Id);
        var accountIds = localAccountIds
            .Concat(_snapshots.Keys)
            .Concat(_automaticQueues.Keys)
            .Concat(_messageStates.Keys.Select(static key => key.AccountId))
            .Distinct()
            .ToArray();
        foreach (var accountId in accountIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await jobRegistry.CancelAndWaitAsync(accountId).ConfigureAwait(false);
        }

        _snapshots.Clear();
        _messageStates.Clear();
        _automaticQueues.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        foreach (var accountId in _snapshots.Keys)
            await jobRegistry.CancelAndWaitAsync(accountId).ConfigureAwait(false);
        _initializeLock.Dispose();
    }

    private void StartWorker(Guid accountId, SemanticIndexPlan plan, bool notifyWhenCompleted)
    {
        if (jobRegistry.TryStart(accountId, token => RunBackfillAsync(accountId, plan, notifyWhenCompleted, token), out _))
            SetSnapshot(new(accountId, SemanticIndexJobStatus.Queued, 0, plan.MissingMessageCount));
    }

    private async Task RunBackfillAsync(
        Guid accountId,
        SemanticIndexPlan plan,
        bool notifyWhenCompleted,
        CancellationToken cancellationToken)
    {
        try
        {
            var account = await RequireAccountAsync(accountId).ConfigureAwait(false);
            var mailbox = await RequireMailboxAsync(account, cancellationToken).ConfigureAwait(false);
            var winoAccount = await databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("A Wino account is required for mail intelligence.");
            var candidates = await messageResolver.GetCandidatesAsync(
                account.Id,
                plan.CutoffUtc,
                plan.ThroughUtcExclusive,
                cancellationToken).ConfigureAwait(false);
            var missingIds = (await apiClient.ResolveIntelligenceDeltaAsync(
                mailbox.MailboxId,
                candidates.Select(candidate => candidate.RemoteMessageId).ToArray(),
                plan.CutoffUtc,
                plan.ThroughUtcExclusive,
                cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            var waiting = candidates.Where(candidate => missingIds.Contains(candidate.RemoteMessageId)).ToList();
            var completed = 0;
            var embeddingFailed = 0;
            var metadataCompleted = 0;
            var metadataFailed = 0;
            void ReportProgress(SemanticIndexJobStatus status = SemanticIndexJobStatus.Indexing)
                => SetSnapshot(new(
                    accountId,
                    status,
                    Volatile.Read(ref completed),
                    waiting.Count,
                    null,
                    Volatile.Read(ref embeddingFailed),
                    Volatile.Read(ref metadataCompleted),
                    Volatile.Read(ref metadataFailed)));
            foreach (var candidate in waiting)
                _messageStates[(accountId, candidate.RemoteMessageId)] = SemanticMessageIndexState.Queued;
            ReportProgress();

            await RunPreparedDocumentPipelineAsync(
                account,
                winoAccount.Id,
                mailbox.MailboxId,
                waiting,
                result =>
                {
                    Interlocked.Add(ref completed, result.SucceededCount);
                    Interlocked.Add(ref embeddingFailed, result.FailedCount);
                    ReportProgress();
                },
                result =>
                {
                    Interlocked.Add(ref metadataCompleted, result.SucceededCount);
                    Interlocked.Add(ref metadataFailed, result.FailedCount);
                    ReportProgress();
                },
                ReportProgress,
                cancellationToken).ConfigureAwait(false);

            await DrainAutomaticQueueAsync(account, mailbox.MailboxId, cancellationToken).ConfigureAwait(false);

            // Ingest responses contain only artifacts created by this run. Downloading from the
            // durable server cursor also restores artifacts that already existed for the selected
            // range, which is essential after the user deletes local intelligence.
            await DownloadAvailableIntelligenceAsync(account.Id, cancellationToken: cancellationToken).ConfigureAwait(false);

            await SaveProfileAsync(mailbox.MailboxId, plan, "completed", cancellationToken).ConfigureAwait(false);
            ReportProgress(SemanticIndexJobStatus.Completed);
            if (notifyWhenCompleted)
                WeakReferenceMessenger.Default.Send(new SemanticIndexingCompleted(account.Id, account.Address, completed));
        }
        catch (OperationCanceledException)
        {
            SetSnapshot(new(accountId, SemanticIndexJobStatus.Cancelled, 0, plan.MissingMessageCount));
            throw;
        }
        catch (Exception exception) when (exception.Message.Contains("AI_QUOTA_EXCEEDED", StringComparison.Ordinal))
        {
            SetSnapshot(new(accountId, SemanticIndexJobStatus.PausedForQuota, 0, plan.MissingMessageCount, "AI_QUOTA_EXCEEDED"));
        }
        catch (Exception exception)
        {
            SetSnapshot(new(accountId, SemanticIndexJobStatus.Failed, 0, plan.MissingMessageCount, exception.Message));
        }
    }

    private async Task RunPreparedDocumentPipelineAsync(
        MailAccount account,
        Guid winoUserId,
        Guid mailboxId,
        IReadOnlyList<IntelligenceMessageCandidate> candidates,
        Action<PipelineBatchResult> embeddingProgress,
        Action<PipelineBatchResult> metadataProgress,
        Action<SemanticIndexJobStatus> statusProgress,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
            return;

        var prepared = Channel.CreateBounded<PreparedDocumentWork>(new BoundedChannelOptions(UploadBatchSize * UploadConcurrency)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        var batches = Channel.CreateBounded<PreparedDocumentWork[]>(new BoundedChannelOptions(UploadConcurrency)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });
        using var pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = pipelineCancellation.Token;

        async Task GuardAsync(Func<Task> operation)
        {
            try { await operation().ConfigureAwait(false); }
            catch
            {
                await pipelineCancellation.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        var producer = GuardAsync(async () =>
        {
            Exception? failure = null;
            try
            {
                await Parallel.ForEachAsync(candidates, new ParallelOptions
                {
                    MaxDegreeOfParallelism = DocumentPreparationConcurrency,
                    CancellationToken = token,
                }, async (candidate, itemToken) =>
                {
                    while (synchronizationManager.IsAccountSynchronizing(account.Id))
                    {
                        statusProgress(SemanticIndexJobStatus.PausedForSynchronization);
                        await Task.Delay(TimeSpan.FromSeconds(1), itemToken).ConfigureAwait(false);
                    }

                    _messageStates[(account.Id, candidate.RemoteMessageId)] = SemanticMessageIndexState.Indexing;
                    var document = await PrepareDocumentAsync(account, candidate, itemToken).ConfigureAwait(false);
                    await prepared.Writer.WriteAsync(new(candidate, document), itemToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }
            finally { prepared.Writer.TryComplete(failure); }
        });

        var batcher = GuardAsync(async () =>
        {
            Exception? failure = null;
            try
            {
                var current = new List<PreparedDocumentWork>(UploadBatchSize);
                await foreach (var item in prepared.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    current.Add(item);
                    if (current.Count < UploadBatchSize)
                        continue;
                    await batches.Writer.WriteAsync(current.ToArray(), token).ConfigureAwait(false);
                    current.Clear();
                }
                if (current.Count > 0)
                    await batches.Writer.WriteAsync(current.ToArray(), token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }
            finally { batches.Writer.TryComplete(failure); }
        });

        var consumers = Enumerable.Range(0, UploadConcurrency).Select(workerIndex => GuardAsync(async () =>
        {
            await foreach (var batch in batches.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var result = await ProcessPreparedBatchAsync(
                    account,
                    winoUserId,
                    mailboxId,
                    batch,
                    embeddingProgress,
                    metadataProgress,
                    token).ConfigureAwait(false);
                foreach (var item in batch)
                {
                    var indexed = result.IndexedRemoteMessageIds.Contains(item.Candidate.RemoteMessageId);
                    _messageStates[(account.Id, item.Candidate.RemoteMessageId)] = indexed
                        ? SemanticMessageIndexState.Indexed
                        : SemanticMessageIndexState.Failed;
                    if (indexed && _automaticQueues.TryGetValue(account.Id, out var automaticQueue))
                        automaticQueue.TryRemove(item.Candidate.RemoteMessageId, out _);
                }
            }
        })).ToArray();

        await Task.WhenAll(consumers.Prepend(batcher).Prepend(producer)).ConfigureAwait(false);
        statusProgress(SemanticIndexJobStatus.Indexing);
    }

    private async Task<BatchProcessingResult> ProcessBatchAsync(
        MailAccount account,
        Guid winoUserId,
        Guid mailboxId,
        IReadOnlyList<IntelligenceMessageCandidate> batch,
        Action<PipelineBatchResult> embeddingProgress,
        Action<PipelineBatchResult> metadataProgress,
        CancellationToken cancellationToken)
    {
        var documents = await PrepareDocumentsAsync(account, batch, cancellationToken).ConfigureAwait(false);
        var work = batch.Select((candidate, index) => new PreparedDocumentWork(candidate, documents[index])).ToArray();
        return await ProcessPreparedBatchAsync(
            account, winoUserId, mailboxId, work, embeddingProgress, metadataProgress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BatchProcessingResult> ProcessPreparedBatchAsync(
        MailAccount account,
        Guid winoUserId,
        Guid mailboxId,
        IReadOnlyList<PreparedDocumentWork> batch,
        Action<PipelineBatchResult> embeddingProgress,
        Action<PipelineBatchResult> metadataProgress,
        CancellationToken cancellationToken)
    {
        var candidates = batch.Select(x => x.Candidate).ToArray();
        var documents = batch.Select(x => x.Document).ToArray();
        var route = $"/api/v1/ai/intelligence/mailboxes/{mailboxId:D}/ingest";
        var request = new IngestIntelligenceRequest
        {
            Language = translationService.CurrentLanguageModel?.Code ?? "en-US",
            Documents = documents.Select(document => new IntelligenceIngestDocumentRequest
            {
                RemoteMessageId = document.ProviderMessageId,
                ContentHash = document.ContentHash,
                CanonicalContent = document.CanonicalContent,
                OccurredAtUtc = document.OccurredAtUtc,
                IsOutgoing = document.IsOutgoing,
                IsRead = document.IsRead,
                IsFlagged = document.IsFlagged,
                HasAttachments = document.HasAttachments,
                IsDirectRecipient = document.IsDirectRecipient,
                HasLaterOutgoingReply = document.HasLaterOutgoingReply,
                ProviderImportance = document.ProviderImportance,
                ProviderFolderIds = document.ProviderFolderIds,
                SenderAddresses = document.SenderAddresses,
                SenderDomains = document.SenderDomains,
            }).ToArray(),
        };
        var envelope = EncryptRequest(
            request,
            WinoAccountApiJsonContext.Default.IngestIntelligenceRequest,
            winoUserId,
            mailboxId,
            route);
        IntelligenceIngestResultDto result;
        try { result = await intelligenceBackend.IngestAsync(mailboxId, envelope, cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(envelope); }

        var successfulRemoteIds = result.Items.Where(item => item.Status == "succeeded")
            .Select(item => item.RemoteMessageId).ToHashSet(StringComparer.Ordinal);
        var succeeded = successfulRemoteIds.Count;
        var progress = new PipelineBatchResult(
            succeeded,
            result.Items.Count - succeeded,
            candidates.Where(candidate => successfulRemoteIds.Contains(candidate.RemoteMessageId))
                .Select(candidate => candidate.UniqueId).ToHashSet());
        embeddingProgress(progress);
        metadataProgress(progress);
        if (result.Artifacts.Count > 0)
        {
            var throughRevision = result.Artifacts.Max(artifact => artifact.ArtifactRevision);
            // An ingest result is not a complete revision feed. Advancing the download cursor here
            // would skip older server artifacts when local intelligence has been deleted.
            await localStore.ImportAsync(
                account.Id,
                mailboxId,
                result.Artifacts,
                throughRevision,
                cancellationToken,
                advanceImportCursor: false).ConfigureAwait(false);
        }
        var confirmedRemoteMessageIds = successfulRemoteIds.ToArray();
        await localStore.DeletePreparedDocumentsAsync(account.Id, confirmedRemoteMessageIds, cancellationToken).ConfigureAwait(false);
        return new BatchProcessingResult(successfulRemoteIds);
    }

    private async Task<IntelligenceIndexDocumentRequest[]> PrepareDocumentsAsync(
        MailAccount account,
        IReadOnlyList<IntelligenceMessageCandidate> batch,
        CancellationToken cancellationToken)
    {
        var documents = new IntelligenceIndexDocumentRequest[batch.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, batch.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = DocumentPreparationConcurrency,
            CancellationToken = cancellationToken,
        }, async (index, token) => documents[index] = await PrepareDocumentAsync(account, batch[index], token).ConfigureAwait(false)).ConfigureAwait(false);
        return documents;
    }

    private async Task<IntelligenceIndexDocumentRequest> PrepareDocumentAsync(
        MailAccount account,
        IntelligenceMessageCandidate candidate,
        CancellationToken cancellationToken)
    {
        var hasLocalMime = false;
        foreach (var fileId in candidate.FileIds)
        {
            if (await mimeFileService.IsMimeExistAsync(account.Id, fileId).ConfigureAwait(false))
            {
                hasLocalMime = true;
                break;
            }
        }

        if (!hasLocalMime)
        {
            var cached = await localStore.GetPreparedDocumentAsync(account.Id, candidate.RemoteMessageId, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
                return cached;
        }

        var content = await messageResolver.GetContentAsync(account.Id, candidate, cancellationToken).ConfigureAwait(false);
        var from = content.From.Count > 0 ? content.From : [new MailAddress(candidate.Sender, candidate.SenderName)];
        var prepared = new MailContentProcessor(new HtmlContentSanitizer()).Prepare(
            from,
            candidate.Subject,
            content.Body,
            EmbeddingProfile.OpenAiTextEmbedding3Small768);
        var senderAddresses = from.Select(x => x.Address).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var document = new IntelligenceIndexDocumentRequest
        {
            ClientCorrelationId = candidate.UniqueId,
            ProviderMessageId = candidate.RemoteMessageId,
            ContentHash = prepared.ContentHash,
            CanonicalContent = prepared.CanonicalText,
            OccurredAtUtc = ToUtc(candidate.ReceivedAt),
            IsOutgoing = candidate.IsOutgoing,
            IsRead = candidate.IsRead,
            IsFlagged = candidate.IsFlagged,
            HasAttachments = candidate.HasAttachments,
            IsDirectRecipient = content.ToRecipients.Any(x => string.Equals(x, account.Address, StringComparison.OrdinalIgnoreCase)),
            HasLaterOutgoingReply = candidate.HasLaterOutgoingReply,
            ProviderImportance = candidate.ProviderImportance,
            ProviderFolderIds = candidate.RemoteFolderIds,
            SenderAddresses = senderAddresses,
            SenderDomains = senderAddresses.Select(x => x[(x.LastIndexOf('@') + 1)..]).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
        await localStore.SavePreparedDocumentAsync(account.Id, candidate.RemoteMessageId, document, cancellationToken).ConfigureAwait(false);
        return document;
    }

    private async Task DrainAutomaticQueueAsync(MailAccount account, Guid mailboxId, CancellationToken cancellationToken)
    {
        var queue = _automaticQueues.GetOrAdd(account.Id, _ => new ConcurrentDictionary<string, byte>());
        while (!queue.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestedIds = queue.Keys.ToHashSet(StringComparer.Ordinal);
            foreach (var id in requestedIds)
                queue.TryRemove(id, out _);
            var candidates = (await messageResolver.GetCandidatesAsync(account.Id, null, cancellationToken).ConfigureAwait(false))
                .Where(x => requestedIds.Contains(x.RemoteMessageId)).ToArray();
            var missingIds = (await apiClient.ResolveIntelligenceDeltaAsync(
                mailboxId,
                candidates.Select(candidate => candidate.RemoteMessageId).ToArray(),
                cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            candidates = candidates.Where(candidate => missingIds.Contains(candidate.RemoteMessageId)).ToArray();
            var winoAccount = await databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("A Wino account is required for mail intelligence.");
            foreach (var batch in candidates.Chunk(UploadBatchSize))
            {
                foreach (var candidate in batch)
                    _messageStates[(account.Id, candidate.RemoteMessageId)] = SemanticMessageIndexState.Indexing;
                SetSnapshot(new(account.Id, SemanticIndexJobStatus.Indexing, 0, candidates.Length));
                var processed = await ProcessBatchAsync(
                    account,
                    winoAccount.Id,
                    mailboxId,
                    batch,
                    embedding => SetSnapshot(new(
                        account.Id,
                        SemanticIndexJobStatus.Indexing,
                        embedding.SucceededCount,
                        candidates.Length,
                        null,
                        embedding.FailedCount)),
                    metadata =>
                    {
                        var current = GetJobSnapshot(account.Id);
                        SetSnapshot(current with
                        {
                            MetadataCompletedMessageCount = metadata.SucceededCount,
                            MetadataFailedMessageCount = metadata.FailedCount,
                        });
                    },
                    cancellationToken).ConfigureAwait(false);
                var indexed = processed.IndexedRemoteMessageIds;
                foreach (var candidate in batch)
                    _messageStates[(account.Id, candidate.RemoteMessageId)] = indexed.Contains(candidate.RemoteMessageId)
                        ? SemanticMessageIndexState.Indexed
                        : SemanticMessageIndexState.Failed;
            }
            SetSnapshot(new(account.Id, SemanticIndexJobStatus.Completed, candidates.Length, candidates.Length));
        }
    }

    private void QueueAutomaticMessage(Guid accountId, string remoteMessageId)
    {
        _automaticQueues.GetOrAdd(accountId, _ => new ConcurrentDictionary<string, byte>())[remoteMessageId] = 0;
        _messageStates[(accountId, remoteMessageId)] = SemanticMessageIndexState.Queued;
    }

    private async Task EnsureAutomaticQueueDrainedAsync(MailAccount account, Guid mailboxId)
    {
        var queue = _automaticQueues.GetOrAdd(account.Id, _ => new ConcurrentDictionary<string, byte>());
        while (!queue.IsEmpty)
        {
            if (jobRegistry.TryStart(
                    account.Id,
                    token => RunAutomaticQueueAsync(account, mailboxId, token),
                    out var completion))
            {
                SetSnapshot(new(account.Id, SemanticIndexJobStatus.Queued, 0, queue.Count));
            }

            await completion.ConfigureAwait(false);
        }
    }

    private async Task RunAutomaticQueueAsync(MailAccount account, Guid mailboxId, CancellationToken cancellationToken)
    {
        try
        {
            await DrainAutomaticQueueAsync(account, mailboxId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetSnapshot(new(account.Id, SemanticIndexJobStatus.Cancelled, 0, 0));
            throw;
        }
        catch (Exception exception)
        {
            SetSnapshot(new(account.Id, SemanticIndexJobStatus.Failed, 0, 0, exception.Message));
        }
    }

    private async Task<SemanticIndexPlan> CalculatePlanCoreAsync(
        MailAccount account,
        SemanticIndexRangePreset preset,
        DateTimeOffset? cutoffUtc,
        DateTimeOffset? throughUtcExclusive,
        bool automaticallyIndexNewMessages,
        CancellationToken cancellationToken)
    {
        var candidates = await messageResolver.GetCandidatesAsync(account.Id, cutoffUtc, throughUtcExclusive, cancellationToken).ConfigureAwait(false);
        var missing = candidates.Count;
        return new SemanticIndexPlan(
            account.Id,
            preset,
            cutoffUtc,
            throughUtcExclusive,
            automaticallyIndexNewMessages,
            candidates.Count,
            missing,
            TimeSpan.FromSeconds(missing * 0.6),
            false);
    }

    private async Task<SemanticIndexAccountState> GetStateCoreAsync(Guid localMailAccountId, CancellationToken cancellationToken)
    {
        var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
        var mailbox = await FindMailboxAsync(account, cancellationToken).ConfigureAwait(false);
        if (mailbox is null && account.Preferences.IsSemanticIndexingEnabled)
            mailbox = await apiClient.EnsureSemanticMailboxAsync(account.Address, (int)account.ProviderType, cancellationToken).ConfigureAwait(false);
        if (mailbox is null)
            return new(account.Preferences.IsSemanticIndexingEnabled, null, null, 0, 0, false, false, false);

        var status = await apiClient.GetIntelligenceStatusAsync(mailbox.MailboxId, cancellationToken).ConfigureAwait(false);
        var localRevision = await localStore.GetLastImportedRevisionAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var syntheticState = new SemanticMailboxIndexStateDto(
            status.MailboxId,
            status.ActiveEmbeddingModelId,
            status.ActiveEmbeddingModelId,
            0,
            status.OldestReceivedAtUtc,
            status.NewestReceivedAtUtc,
            status.IndexedMessageCount,
            status.StorageSizeBytes,
            status.CurrentRevision,
            DateTimeOffset.UtcNow);
        var hasData = status.StorageSizeBytes > 0;
        var canDownload = status.CurrentRevision > localRevision;
        var isUpToDate = localRevision >= status.CurrentRevision;
        var localIndexedMessageCount = isUpToDate
            ? checked((int)Math.Min(status.IndexedMessageCount, int.MaxValue))
            : 0;
        return new(
            account.Preferences.IsSemanticIndexingEnabled,
            mailbox.MailboxId,
            syntheticState,
            localRevision,
            0,
            hasData,
            isUpToDate,
            canDownload,
            localIndexedMessageCount,
            null);
    }

    private async Task SaveProfileAsync(Guid mailboxId, SemanticIndexPlan plan, string status, CancellationToken cancellationToken)
    {
        var updatedAtUtc = DateTimeOffset.UtcNow;
        if (status == "completed" && plan.ThroughUtcExclusive is not null)
        {
            var existing = (await localStore.GetJobIntentsAsync(cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(x => x.LocalAccountId == plan.LocalAccountId);
            if (existing?.BackfillStatus == "in-progress" && existing.ThroughUtcExclusive == plan.ThroughUtcExclusive)
                updatedAtUtc = existing.UpdatedAtUtc;
        }

        await localStore.SaveJobIntentAsync(new SemanticIndexJobIntent(
            plan.LocalAccountId,
            mailboxId,
            plan.RangePreset,
            plan.CutoffUtc,
            plan.ThroughUtcExclusive,
            plan.AutomaticallyIndexNewMessages,
            status,
            updatedAtUtc), cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveLocalProfileAsync(Guid accountId, Guid mailboxId, IntelligenceIndexingProfileDto profile, CancellationToken cancellationToken)
    {
        var existing = (await localStore.GetJobIntentsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(x => x.LocalAccountId == accountId && x.ServerMailboxId == mailboxId);
        var preserveBoundedRange = existing?.ThroughUtcExclusive is not null;
        await localStore.SaveJobIntentAsync(new SemanticIndexJobIntent(
            accountId,
            mailboxId,
            SemanticIndexRangePresetExtensions.FromStableId(profile.RangePresetId),
            profile.CutoffUtc,
            preserveBoundedRange ? existing!.ThroughUtcExclusive : null,
            profile.AutomaticallyIndexNewMessages,
            profile.BackfillStatus,
            preserveBoundedRange ? existing!.UpdatedAtUtc : profile.UpdatedAtUtc), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSynchronizationCompletedAsync(AccountSynchronizationCompleted message)
    {
        try
        {
            if (message.Result is SynchronizationCompletedState.Canceled or SynchronizationCompletedState.Failed)
                return;
            if (!await localIntelligenceService.ShouldAutomaticallyProcessAsync(message.AccountId).ConfigureAwait(false))
                return;
            var intent = (await localStore.GetJobIntentsAsync().ConfigureAwait(false))
                .Single(x => x.LocalAccountId == message.AccountId);
            var account = await accountService.GetAccountAsync(message.AccountId).ConfigureAwait(false);

            var candidates = await messageResolver.GetCandidatesAsync(
                account.Id,
                intent.UpdatedAtUtc,
                throughUtcExclusive: null,
                CancellationToken.None).ConfigureAwait(false);
            if (candidates.Count == 0)
                return;

            var mailbox = await RequireMailboxAsync(account, CancellationToken.None).ConfigureAwait(false);
            foreach (var candidate in candidates)
                QueueAutomaticMessage(account.Id, candidate.RemoteMessageId);
            _ = EnsureAutomaticQueueDrainedAsync(account, mailbox.MailboxId);
        }
        catch
        {
            // Background synchronization must not fail because intelligence is unavailable.
        }
    }

    private void SetSnapshot(SemanticIndexJobSnapshot snapshot)
    {
        _snapshots[snapshot.LocalAccountId] = snapshot;
        WeakReferenceMessenger.Default.Send(new SemanticIndexJobChanged(snapshot.LocalAccountId, snapshot));
    }

    private sealed record PreparedDocumentWork(
        IntelligenceMessageCandidate Candidate,
        IntelligenceIndexDocumentRequest Document);
    private sealed record PipelineBatchResult(
        int SucceededCount,
        int FailedCount,
        IReadOnlySet<Guid> SucceededMessageIds);
    private sealed record BatchProcessingResult(IReadOnlySet<string> IndexedRemoteMessageIds);

    private async Task<SemanticMailboxDto?> FindMailboxAsync(MailAccount account, CancellationToken cancellationToken)
    {
        var mailboxes = await apiClient.GetSemanticMailboxesAsync(cancellationToken).ConfigureAwait(false);
        return mailboxes.SingleOrDefault(x => x.ProviderType == (int)account.ProviderType &&
            string.Equals(x.Address.Trim(), account.Address.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<SemanticMailboxDto> RequireMailboxAsync(MailAccount account, CancellationToken cancellationToken)
        => await FindMailboxAsync(account, cancellationToken).ConfigureAwait(false)
            ?? await apiClient.EnsureSemanticMailboxAsync(account.Address, (int)account.ProviderType, cancellationToken).ConfigureAwait(false);

    private byte[] EncryptRequest<T>(
        T request,
        JsonTypeInfo<T> typeInfo,
        Guid winoUserId,
        Guid mailboxId,
        string route)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(request, typeInfo);
        try
        {
            var encrypted = envelopeEncryptor.Encrypt(
                plaintext,
                new ContentEnvelopeContext(winoUserId, mailboxId, route),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow);
            try
            {
                return ContentEnvelopeBinaryCodec.Encode(encrypted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted.WrappedKey);
                CryptographicOperations.ZeroMemory(encrypted.Nonce);
                CryptographicOperations.ZeroMemory(encrypted.Tag);
                CryptographicOperations.ZeroMemory(encrypted.Ciphertext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private async Task<MailAccount> RequireAccountAsync(Guid accountId)
        => await accountService.GetAccountAsync(accountId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The mail account no longer exists.");

    private static bool IsNarrower(DateTimeOffset? existingCutoff, DateTimeOffset? newCutoff)
        => newCutoff is not null && (existingCutoff is null || newCutoff > existingCutoff);

    private static DateTimeOffset ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value),
        DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime()),
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
    };

}
