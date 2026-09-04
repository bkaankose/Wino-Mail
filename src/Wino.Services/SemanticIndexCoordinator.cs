#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
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
using Serilog;

namespace Wino.Services;

public sealed class SemanticIndexCoordinator(
    IDatabaseService databaseService,
    IAccountService accountService,
    IWinoAccountApiClient apiClient,
    ILocalIntelligenceStore localStore,
    ILocalIntelligenceService localIntelligenceService,
    IContentEnvelopeEncryptor envelopeEncryptor,
    ISemanticIndexJobRegistry jobRegistry,
    ITranslationService translationService,
    IIntelligenceMessageContextResolver messageResolver,
    IMessenger messenger) : ISemanticIndexCoordinator, IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<SemanticIndexCoordinator>();
    private const int ReconciliationBatchSize = 1_000;
    private const int UploadBatchSize = 20;
    private const int DocumentPreparationConcurrency = 4;

    private readonly ConcurrentDictionary<Guid, SemanticIndexJobSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<(Guid AccountId, string MessageId), SemanticMessageIndexState> _messageStates = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _automaticQueues = new();
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _synchronizedMailQueues = new();
    private readonly ConcurrentDictionary<Guid, byte> _headlineTranslations = new();

    /// <summary>
    /// In-flight manual single-message runs, so repeated clicks on the same message join the run
    /// already going instead of ingesting it twice and billing the user twice for it.
    /// </summary>
    private readonly ConcurrentDictionary<(Guid AccountId, string RemoteMessageId), Lazy<Task>> _singleMessageRuns = new();
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _initialized;

    public async Task InitializeAsync()
    {

        if (_initialized)
            return;

        await _initializeLock.WaitAsync().ConfigureAwait(false);

        try
        {
            if (_initialized)
                return;

            messenger.Register<AccountSynchronizationCompleted>(this, static (recipient, message) =>
                _ = ((SemanticIndexCoordinator)recipient).HandleSynchronizationCompletedAsync(message));
            messenger.Register<MailAddedMessage>(this, static (recipient, message) =>
                ((SemanticIndexCoordinator)recipient).CaptureSynchronizedMails([message.AddedMail], message.Source));
            messenger.Register<BulkMailAddedMessage>(this, static (recipient, message) =>
                ((SemanticIndexCoordinator)recipient).CaptureSynchronizedMails(message.AddedMails, message.Source));

            _initialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task StartIndexingAsync(
        Guid localMailAccountId,
        IReadOnlyCollection<string> remoteMessageIds,
        CancellationToken cancellationToken = default,
        bool notifyWhenCompleted = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ids = NormalizeIds(remoteMessageIds);
        if (ids.Length == 0)
            throw new ArgumentException("At least one message must be selected.", nameof(remoteMessageIds));

        var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
        if (!account.Preferences.IsSemanticIndexingEnabled)
            throw new InvalidOperationException("Mail intelligence is not enabled for this account.");

        if (_headlineTranslations.ContainsKey(localMailAccountId))
            throw new InvalidOperationException("Headline translation is already in progress for this mailbox.");

        StartWorker(account, ids, notifyWhenCompleted);
    }

    public async Task<HeadlineTranslationResultDto> TranslateHeadlinesAsync(
        Guid localMailAccountId,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (jobRegistry.IsRunning(localMailAccountId) || !_headlineTranslations.TryAdd(localMailAccountId, 0))
            throw new InvalidOperationException("Indexing and headline translation cannot run at the same time.");

        try
        {
            var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
            var mailbox = await RequireMailboxAsync(account, cancellationToken).ConfigureAwait(false);

            await localStore.SetHeadlineLanguageAsync(
                account.Id,
                mailbox.MailboxId,
                targetLanguage,
                cancellationToken).ConfigureAwait(false);

            SetSnapshot(new(localMailAccountId, SemanticIndexJobStatus.TranslatingHeadlines, 0, 0));

            var result = await apiClient.TranslateBriefingHeadlinesAsync(
                mailbox.MailboxId,
                targetLanguage,
                cancellationToken).ConfigureAwait(false);

            SetSnapshot(new(
                localMailAccountId,
                SemanticIndexJobStatus.Completed,
                result.TranslatedCount,
                result.TranslatedCount + result.FailedCount,
                FailedMessageCount: result.FailedCount));

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

    /// <summary>
    /// Indexes one message on demand, outside the batch pipeline.
    /// </summary>
    /// <remarks>
    /// Deliberately does not take the job registry lock. Asking for a single message is a direct
    /// request from someone looking at that message, so it must not fail because a batch run
    /// happens to hold the lock — which is exactly what it used to do, reporting "indexing is
    /// already in progress" for a mailbox the user was only trying to read.
    /// <para>
    /// For the same reason it publishes no job snapshots: a one-message run has no meaningful
    /// progress to show, and emitting snapshots would overwrite the running batch job's progress
    /// with a two-step bar that finishes immediately.
    /// </para>
    /// </remarks>
    public async Task IndexMessageAsync(
        Guid localMailAccountId,
        string mailUniqueId,
        CancellationToken cancellationToken = default)
    {
        var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
        if (!account.Preferences.IsSemanticIndexingEnabled)
            throw new InvalidOperationException("Mail intelligence is not enabled for this account.");

        var candidate = await messageResolver.FindCandidateAsync(
            account.Id,
            mailUniqueId,
            cancellationToken).ConfigureAwait(false);

        if (candidate is null)
            throw new InvalidOperationException("This message cannot be indexed.");

        var key = (account.Id, candidate.RemoteMessageId);

        // The shared run is started without the caller's token: one caller walking away must not
        // cancel the work another caller is still waiting on. The caller's token governs its own
        // wait instead.
        var run = _singleMessageRuns.GetOrAdd(key, _ => new Lazy<Task>(
            () => IndexSingleMessageAsync(account, candidate, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            await run.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (run.Value.IsCompleted)
                _singleMessageRuns.TryRemove(key, out _);
        }

        if (GetMessageState(account.Id, candidate) != SemanticMessageIndexState.Indexed)
            throw new InvalidOperationException("The message could not be indexed.");
    }

    /// <summary>
    /// The single-message ingest pipeline: reconcile that one id, and upload it only if the cloud
    /// does not already hold it.
    /// </summary>
    private async Task IndexSingleMessageAsync(
        MailAccount account,
        IntelligenceMessageCandidate candidate,
        CancellationToken cancellationToken)
    {
        var mailbox = await RequireMailboxHeadAsync(account, cancellationToken).ConfigureAwait(false);
        var key = (account.Id, candidate.RemoteMessageId);

        // Reconcile first: a message already indexed on another device costs a download here
        // rather than a fresh ingest.
        var reconciled = await ReconcileBatchAsync(
            mailbox,
            [candidate.RemoteMessageId],
            cancellationToken).ConfigureAwait(false);

        await SynchronizeChangesAsync(account.Id, mailbox, cancellationToken).ConfigureAwait(false);

        if (reconciled.CoveredServerMessageKeys.Contains(candidate.RemoteMessageId, StringComparer.Ordinal))
        {
            _messageStates[key] = SemanticMessageIndexState.Indexed;
            return;
        }

        _messageStates[key] = SemanticMessageIndexState.Indexing;

        try
        {
            var winoAccount = await databaseService.Connection.Table<WinoAccount>()
                .FirstOrDefaultAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("A Wino account is required for mail intelligence.");
            var document = await PrepareDocumentAsync(account, candidate, cancellationToken).ConfigureAwait(false);
            var ingestResult = await IngestBatchAsync(
                winoAccount.Id,
                mailbox,
                [new PreparedDocument(candidate, document)],
                cancellationToken).ConfigureAwait(false);

            await SynchronizeChangesAsync(account.Id, mailbox, cancellationToken).ConfigureAwait(false);

            var succeeded = ingestResult.Items.Any(item =>
                item.Status is MessageIntelligenceIngestionItemStatuses.Indexed or MessageIntelligenceIngestionItemStatuses.AlreadyIndexed &&
                string.Equals(item.ServerMessageKey, candidate.RemoteMessageId, StringComparison.Ordinal));
            _messageStates[key] = succeeded
                ? SemanticMessageIndexState.Indexed
                : SemanticMessageIndexState.Failed;
        }
        catch
        {
            _messageStates[key] = SemanticMessageIndexState.Failed;
            throw;
        }
    }

    public SemanticIndexJobSnapshot GetJobSnapshot(Guid localMailAccountId)
        => _snapshots.TryGetValue(localMailAccountId, out var snapshot)
            ? snapshot
            : new SemanticIndexJobSnapshot(localMailAccountId, SemanticIndexJobStatus.Idle, 0, 0);

    public async Task<SemanticMessageIndexState> GetMessageStateAsync(
        Guid localMailAccountId,
        string mailUniqueId,
        CancellationToken cancellationToken = default)
    {
        var account = await accountService.GetAccountAsync(localMailAccountId).ConfigureAwait(false);
        if (account is null || !account.Preferences.IsSemanticIndexingEnabled)
            return SemanticMessageIndexState.Unsupported;

        var candidate = await messageResolver.FindCandidateAsync(
            account.Id,
            mailUniqueId,
            cancellationToken).ConfigureAwait(false);
        if (candidate is null)
            return SemanticMessageIndexState.Unsupported;

        var documents = await localStore.GetCurrentDocumentsAsync(
            localMailAccountId,
            [candidate.RemoteMessageId],
            cancellationToken).ConfigureAwait(false);
        if (documents.ContainsKey(candidate.RemoteMessageId))
            return SemanticMessageIndexState.Indexed;

        return GetMessageState(account.Id, candidate);
    }

    public Task<SemanticIndexAccountState> GetStateAsync(
        Guid localMailAccountId,
        CancellationToken cancellationToken = default)
        => GetStateCoreAsync(localMailAccountId, cancellationToken);

    public async Task EnsureMailboxAsync(
        Guid localMailAccountId,
        CancellationToken cancellationToken = default)
    {
        var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
        await RequireMailboxHeadAsync(account, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IntelligenceDownloadResult> DownloadAvailableIntelligenceAsync(
        Guid localMailAccountId,
        IProgress<SemanticIndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
        var mailbox = await RequireMailboxHeadAsync(account, cancellationToken).ConfigureAwait(false);
        await SynchronizeChangesAsync(account.Id, mailbox, cancellationToken).ConfigureAwait(false);
        var candidates = await messageResolver.GetCandidatesAsync(
            account.Id,
            cutoffUtc: null,
            cancellationToken).ConfigureAwait(false);
        var selected = candidates.Select(static candidate => candidate.RemoteMessageId).Distinct(StringComparer.Ordinal).ToArray();
        var documents = await localStore.GetCurrentDocumentsAsync(account.Id, selected, cancellationToken).ConfigureAwait(false);

        progress?.Report(new(documents.Count, selected.Length));
        return new IntelligenceDownloadResult(documents.Keys.ToHashSet(StringComparer.Ordinal), documents.Count);
    }

    public async Task DeleteIndexAsync(
        Guid localMailAccountId,
        CancellationToken cancellationToken = default)
    {
        await jobRegistry.CancelAndWaitAsync(localMailAccountId).ConfigureAwait(false);

        var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);

        try
        {
            var mailbox = await FindMailboxAsync(account, cancellationToken).ConfigureAwait(false);
            if (mailbox is not null)
                await apiClient.DeleteIntelligenceAsync(mailbox.MailboxId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await localStore.DeleteMailboxAsync(localMailAccountId, cancellationToken).ConfigureAwait(false);
            ClearAccountState(localMailAccountId);
        }
    }

    public async Task DeleteLocalIndexAsync(
        Guid localMailAccountId,
        CancellationToken cancellationToken = default)
    {
        await jobRegistry.CancelAndWaitAsync(localMailAccountId).ConfigureAwait(false);
        await localStore.DeleteMailboxAsync(localMailAccountId, cancellationToken).ConfigureAwait(false);
        ClearAccountState(localMailAccountId);
    }

    public async Task ResetLocalStateAsync(CancellationToken cancellationToken = default)
    {
        foreach (var accountId in _snapshots.Keys.Concat(_automaticQueues.Keys).Distinct())
            await jobRegistry.CancelAndWaitAsync(accountId).ConfigureAwait(false);

        _snapshots.Clear();
        _messageStates.Clear();
        _automaticQueues.Clear();
        _synchronizedMailQueues.Clear();
        _headlineTranslations.Clear();

        await localStore.DeleteAccessSnapshotsAsync(cancellationToken).ConfigureAwait(false);
        await localStore.DeleteAccountIntelligenceSnapshotsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        messenger.UnregisterAll(this);

        foreach (var accountId in _snapshots.Keys.Concat(_automaticQueues.Keys).Distinct())
            await jobRegistry.CancelAndWaitAsync(accountId).ConfigureAwait(false);

        _initializeLock.Dispose();
    }

    private void StartWorker(MailAccount account, IReadOnlyCollection<string> remoteMessageIds, bool notifyWhenCompleted)
    {
        if (!jobRegistry.TryStart(
                account.Id,
                token => RunIndexingAsync(account, remoteMessageIds, notifyWhenCompleted, token),
                out _))
        {
            throw new InvalidOperationException("Indexing is already in progress for this mailbox.");
        }
    }

    private async Task RunIndexingAsync(
        MailAccount account,
        IReadOnlyCollection<string> remoteMessageIds,
        bool notifyWhenCompleted,
        CancellationToken cancellationToken)
    {
        var selectedCount = NormalizeIds(remoteMessageIds).Length;
        SetSnapshot(new(
            account.Id,
            SemanticIndexJobStatus.Queued,
            0,
            selectedCount));

        try
        {
            var mailbox = await RequireMailboxHeadAsync(account, cancellationToken).ConfigureAwait(false);
            var result = await ReconcileAsync(
                account,
                mailbox,
                remoteMessageIds,
                uploadMissing: true,
                cancellationToken).ConfigureAwait(false);

            SetSnapshot(new(
                account.Id,
                SemanticIndexJobStatus.Completed,
                result.UploadedCount,
                selectedCount,
                FailedMessageCount: result.FailedCount,
                RestoredMessageCount: result.CoveredRemoteMessageIds.Count));

            if (notifyWhenCompleted)
                messenger.Send(new SemanticIndexingCompleted(account.Id, account.Address, result.UploadedCount));
        }
        catch (OperationCanceledException)
        {
            var current = GetJobSnapshot(account.Id);
            SetSnapshot(current with
            {
                Status = SemanticIndexJobStatus.Cancelled,
                SelectedMessageCount = selectedCount,
            });
            throw;
        }
        catch (Exception exception) when (exception.Message.Contains("AI_QUOTA_EXCEEDED", StringComparison.Ordinal))
        {
            var current = GetJobSnapshot(account.Id);
            SetSnapshot(current with
            {
                Status = SemanticIndexJobStatus.PausedForQuota,
                SelectedMessageCount = selectedCount,
                ErrorCode = "AI_QUOTA_EXCEEDED",
            });
        }
        catch (Exception exception)
        {
            var current = GetJobSnapshot(account.Id);
            SetSnapshot(current with
            {
                Status = SemanticIndexJobStatus.Failed,
                SelectedMessageCount = selectedCount,
                ErrorCode = exception.Message,
            });
        }
    }

    private async Task<ReconciliationRunResult> ReconcileAsync(
        MailAccount account,
        MailboxContext mailbox,
        IReadOnlyCollection<string> remoteMessageIds,
        bool uploadMissing,
        CancellationToken cancellationToken)
    {
        var totalTimer = Stopwatch.StartNew();
        var reconciliationMs = 0d;
        var preparationMs = 0d;
        var ingestionMs = 0d;
        var changeSyncMs = 0d;
        var distinctIds = NormalizeIds(remoteMessageIds);
        var missingIds = new HashSet<string>(StringComparer.Ordinal);
        var restoredIds = new HashSet<string>(StringComparer.Ordinal);

        // Only an upload run is a job. A read-only restore reports progress through its own
        // IProgress and must not publish job snapshots: they would render as indexing having
        // started, and the read-only path returns below without ever publishing a terminal one,
        // so that phantom job would never clear.
        if (uploadMissing)
            SetSnapshot(new(account.Id, SemanticIndexJobStatus.Calculating, 0, distinctIds.Length));

        foreach (var batch in distinctIds.Chunk(ReconciliationBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var phaseTimer = Stopwatch.StartNew();
            var result = await ReconcileBatchAsync(
                mailbox,
                batch,
                cancellationToken).ConfigureAwait(false);
            reconciliationMs += phaseTimer.Elapsed.TotalMilliseconds;

            missingIds.UnionWith(result.MissingServerMessageKeys);
            restoredIds.UnionWith(result.CoveredServerMessageKeys);
            phaseTimer.Restart();
            await SynchronizeChangesAsync(account.Id, mailbox, cancellationToken).ConfigureAwait(false);
            changeSyncMs += phaseTimer.Elapsed.TotalMilliseconds;

            if (uploadMissing)
            {
                SetSnapshot(new(
                    account.Id,
                    SemanticIndexJobStatus.Calculating,
                    0,
                    distinctIds.Length,
                    RestoredMessageCount: restoredIds.Count));
            }
        }

        foreach (var remoteMessageId in restoredIds)
            _messageStates[(account.Id, remoteMessageId)] = SemanticMessageIndexState.Indexed;

        if (!uploadMissing || missingIds.Count == 0)
        {
            LogPerformance(distinctIds.Length, restoredIds.Count, 0, 0, reconciliationMs, preparationMs, ingestionMs, changeSyncMs, totalTimer.Elapsed.TotalMilliseconds);
            return new(restoredIds, restoredIds.Count, 0, 0);
        }

        var missingCandidates = await ResolveCandidatesAsync(
            account.Id,
            missingIds,
            cancellationToken).ConfigureAwait(false);
        foreach (var candidate in missingCandidates)
            _messageStates[(account.Id, candidate.RemoteMessageId)] = SemanticMessageIndexState.Queued;

        var winoAccount = await databaseService.Connection.Table<WinoAccount>()
            .FirstOrDefaultAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("A Wino account is required for mail intelligence.");
        var uploaded = 0;
        var failed = missingIds.Count - missingCandidates.Count;

        foreach (var batch in missingCandidates.Chunk(UploadBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var candidate in batch)
                _messageStates[(account.Id, candidate.RemoteMessageId)] = SemanticMessageIndexState.Indexing;

            SetSnapshot(new(
                account.Id,
                SemanticIndexJobStatus.Indexing,
                uploaded,
                distinctIds.Length,
                FailedMessageCount: failed,
                RestoredMessageCount: restoredIds.Count));

            var phaseTimer = Stopwatch.StartNew();
            var prepared = await PrepareBatchAsync(account, batch, cancellationToken).ConfigureAwait(false);
            preparationMs += phaseTimer.Elapsed.TotalMilliseconds;
            failed += batch.Length - prepared.Length;

            if (prepared.Length == 0)
                continue;

            SetSnapshot(new(
                account.Id,
                SemanticIndexJobStatus.GeneratingInsights,
                uploaded,
                distinctIds.Length,
                FailedMessageCount: failed,
                RestoredMessageCount: restoredIds.Count));

            phaseTimer.Restart();
            var ingestResult = await IngestBatchAsync(
                winoAccount.Id,
                mailbox,
                prepared,
                cancellationToken,
                progress =>
                {
                    var terminalByKey = progress.Items.ToDictionary(static item => item.ServerMessageKey, StringComparer.Ordinal);
                    foreach (var item in prepared)
                    {
                        if (!terminalByKey.TryGetValue(item.Candidate.RemoteMessageId, out var terminal))
                            continue;

                        _messageStates[(account.Id, item.Candidate.RemoteMessageId)] = terminal.Status is
                            MessageIntelligenceIngestionItemStatuses.Indexed or MessageIntelligenceIngestionItemStatuses.AlreadyIndexed
                                ? SemanticMessageIndexState.Indexed
                                : SemanticMessageIndexState.Failed;
                    }

                    SetSnapshot(new(
                        account.Id,
                        SemanticIndexJobStatus.GeneratingInsights,
                        uploaded + progress.IndexedCount,
                        distinctIds.Length,
                        FailedMessageCount: failed + progress.FailedCount,
                        RestoredMessageCount: restoredIds.Count + progress.AlreadyIndexedCount));
                }).ConfigureAwait(false);
            ingestionMs += phaseTimer.Elapsed.TotalMilliseconds;
            var newlyIndexedIds = ingestResult.Items
                .Where(static item => item.Status == MessageIntelligenceIngestionItemStatuses.Indexed)
                .Select(static item => item.ServerMessageKey)
                .ToHashSet(StringComparer.Ordinal);
            var alreadyIndexedIds = ingestResult.Items
                .Where(static item => item.Status == MessageIntelligenceIngestionItemStatuses.AlreadyIndexed)
                .Select(static item => item.ServerMessageKey)
                .ToHashSet(StringComparer.Ordinal);
            var successfulIds = newlyIndexedIds.Concat(alreadyIndexedIds).ToHashSet(StringComparer.Ordinal);

            uploaded += newlyIndexedIds.Count;
            restoredIds.UnionWith(alreadyIndexedIds);
            failed += ingestResult.FailedCount;

            phaseTimer.Restart();
            await SynchronizeChangesAsync(account.Id, mailbox, cancellationToken).ConfigureAwait(false);
            changeSyncMs += phaseTimer.Elapsed.TotalMilliseconds;

            foreach (var item in prepared)
            {
                _messageStates[(account.Id, item.Candidate.RemoteMessageId)] = successfulIds.Contains(item.Candidate.RemoteMessageId)
                    ? SemanticMessageIndexState.Indexed
                    : SemanticMessageIndexState.Failed;
            }

            SetSnapshot(new(
                account.Id,
                SemanticIndexJobStatus.Indexing,
                uploaded,
                distinctIds.Length,
                FailedMessageCount: failed,
                RestoredMessageCount: restoredIds.Count));
        }

        totalTimer.Stop();
        LogPerformance(distinctIds.Length, restoredIds.Count, uploaded, failed, reconciliationMs, preparationMs, ingestionMs, changeSyncMs, totalTimer.Elapsed.TotalMilliseconds);
        return new(restoredIds, restoredIds.Count, uploaded, failed);
    }

    private static void LogPerformance(int selected, int restored, int uploaded, int failed, double reconciliationMs, double preparationMs, double ingestionMs, double changeSyncMs, double totalMs)
        => Logger.Information(
            "Intelligence reconciliation completed. SelectedCount={SelectedCount} RestoredCount={RestoredCount} NewMessageCount={NewMessageCount} FailedCount={FailedCount} ReconciliationMs={ReconciliationMs} PreparationMs={PreparationMs} IngestionMs={IngestionMs} ChangeSyncMs={ChangeSyncMs} TotalMs={TotalMs}",
            selected, restored, uploaded, failed, reconciliationMs, preparationMs, ingestionMs, changeSyncMs, totalMs);

    private Task<ReconcileMessageIntelligenceResultDto> ReconcileBatchAsync(
        MailboxContext mailbox,
        IReadOnlyList<string> remoteMessageIds,
        CancellationToken cancellationToken)
        => apiClient.ReconcileMessageIntelligenceAsync(
            mailbox.Mailbox.MailboxId,
            new ReconcileMessageIntelligenceRequest(
                mailbox.Head.IntelligenceVersion,
                mailbox.Head.IndexEpoch,
                remoteMessageIds),
            cancellationToken);

    private async Task<PreparedDocument[]> PrepareBatchAsync(
        MailAccount account,
        IReadOnlyCollection<IntelligenceMessageCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var prepared = new ConcurrentBag<PreparedDocument>();

        await Parallel.ForEachAsync(candidates, new ParallelOptions
        {
            MaxDegreeOfParallelism = DocumentPreparationConcurrency,
            CancellationToken = cancellationToken,
        }, async (candidate, token) =>
        {
            try
            {
                var document = await PrepareDocumentAsync(account, candidate, token).ConfigureAwait(false);
                prepared.Add(new(candidate, document));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _messageStates[(account.Id, candidate.RemoteMessageId)] = SemanticMessageIndexState.Failed;
                Logger.Warning(
                    exception,
                    "Intelligence document preparation failed for account {AccountId}, provider {ProviderType}, remote message {RemoteMessageId}, provider message {ProviderMessageId}.",
                    account.Id,
                    account.ProviderType,
                    candidate.RemoteMessageId,
                    candidate.ProviderMessageId);
            }
        }).ConfigureAwait(false);

        return prepared
            .OrderBy(static item => item.Candidate.RemoteMessageId, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<MessageIntelligenceIngestionJobDto> IngestBatchAsync(
        Guid winoUserId,
        MailboxContext mailbox,
        IReadOnlyCollection<PreparedDocument> batch,
        CancellationToken cancellationToken,
        Action<MessageIntelligenceIngestionJobDto>? progress = null)
    {
        var mailboxId = mailbox.Mailbox.MailboxId;
        var route = $"/api/v1/ai/intelligence/mailboxes/{mailboxId:D}/ingestion-jobs";
        var request = new IngestMessageIntelligenceRequest
        {
            IntelligenceVersion = mailbox.Head.IntelligenceVersion,
            IndexEpoch = mailbox.Head.IndexEpoch,
            Language = translationService.CurrentLanguageModel?.Code ?? "en-US",
            Messages = batch.Select(static item => item.Document).ToArray(),
        };
        var envelope = EncryptRequest(
            request,
            WinoAccountApiJsonContext.Default.IngestMessageIntelligenceRequest,
            winoUserId,
            mailboxId,
            route);

        try
        {
            var accepted = await apiClient.StartMessageIntelligenceIngestionJobAsync(
                mailboxId,
                envelope,
                cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var snapshot = await apiClient.GetMessageIntelligenceIngestionJobAsync(
                    mailboxId,
                    accepted.JobId,
                    cancellationToken).ConfigureAwait(false);
                progress?.Invoke(snapshot);
                if (snapshot.Status == MessageIntelligenceIngestionJobStatuses.Completed)
                    return snapshot;

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private async Task<MessageIntelligenceSourceV1> PrepareDocumentAsync(
        MailAccount account,
        IntelligenceMessageCandidate candidate,
        CancellationToken cancellationToken)
    {
        var content = await messageResolver.GetContentAsync(
            account.Id,
            candidate,
            cancellationToken).ConfigureAwait(false);
        var from = content.From.Count > 0
            ? content.From
            : [new MailAddress(candidate.Sender, candidate.SenderName)];
        var prepared = new MailContentProcessor(new HtmlContentSanitizer()).Prepare(
            from,
            candidate.Subject,
            content.Body,
            EmbeddingProfile.OpenAiTextEmbedding3Small768);
        var senderAddresses = from
            .Select(static address => address.Address)
            .Where(static address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var recipientAddresses = content.ToRecipients
            .Concat(content.CcRecipients)
            .Where(static address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MessageIntelligenceSourceV1
        {
            ServerMessageKey = candidate.RemoteMessageId,
            ContentHash = prepared.ContentHash,
            Subject = candidate.Subject,
            Sender = candidate.Sender,
            Body = content.Body.Content,
            BodyIsHtml = content.Body.Format == MailBodyFormat.Html,
            ReceivedAtUtc = ToUtc(candidate.ReceivedAt),
            IsOutgoing = candidate.IsOutgoing,
            IsRead = candidate.IsRead,
            IsFlagged = candidate.IsFlagged,
            HasAttachments = candidate.HasAttachments,
            IsDirectRecipient = content.ToRecipients.Any(address =>
                string.Equals(address, account.Address, StringComparison.OrdinalIgnoreCase)),
            HasLaterOutgoingReply = candidate.HasLaterOutgoingReply,
            Importance = candidate.ProviderImportance,
            FolderIds = candidate.RemoteFolderIds,
            SenderAddresses = senderAddresses,
            RecipientAddresses = recipientAddresses,
            Attachments = content.Attachments
                .Select(static attachment => new MessageAttachmentMetadataV1(attachment.FileName, attachment.MediaType))
                .ToArray(),
        };
    }

    private async Task<IReadOnlyList<IntelligenceMessageCandidate>> ResolveCandidatesAsync(
        Guid accountId,
        IReadOnlyCollection<string> remoteMessageIds,
        CancellationToken cancellationToken)
    {
        var requested = NormalizeIds(remoteMessageIds).ToHashSet(StringComparer.Ordinal);
        var candidates = await messageResolver.GetCandidatesAsync(
            accountId,
            cutoffUtc: null,
            cancellationToken).ConfigureAwait(false);

        return candidates
            .Where(candidate => requested.Contains(candidate.RemoteMessageId))
            .DistinctBy(static candidate => candidate.RemoteMessageId, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task HandleSynchronizationCompletedAsync(AccountSynchronizationCompleted message)
    {
        if (message.Result is SynchronizationCompletedState.Canceled or SynchronizationCompletedState.Failed)
        {
            ClearSynchronizedMails(message.AccountId);
            return;
        }

        try
        {
            if (!await localIntelligenceService.ShouldAutomaticallyProcessAsync(message.AccountId).ConfigureAwait(false))
            {
                ClearSynchronizedMails(message.AccountId);
                return;
            }

            var ids = TakeSynchronizedMailIds(message.AccountId);
            if (ids.Count == 0)
                return;

            var queue = _automaticQueues.GetOrAdd(
                message.AccountId,
                static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            foreach (var id in ids)
                queue[id] = 0;

            await EnsureAutomaticQueueDrainedAsync(message.AccountId).ConfigureAwait(false);
        }
        catch
        {
            // Intelligence must never fail mail synchronization.
        }
    }

    private async Task EnsureAutomaticQueueDrainedAsync(Guid accountId)
    {
        while (_automaticQueues.TryGetValue(accountId, out var queue) && !queue.IsEmpty)
        {
            var account = await RequireAccountAsync(accountId).ConfigureAwait(false);

            if (jobRegistry.TryStart(accountId, token => DrainAutomaticQueueAsync(account, token), out var completion))
                SetSnapshot(new(accountId, SemanticIndexJobStatus.Queued, 0, queue.Count));

            await completion.ConfigureAwait(false);
        }
    }

    private async Task DrainAutomaticQueueAsync(MailAccount account, CancellationToken cancellationToken)
    {
        while (_automaticQueues.TryGetValue(account.Id, out var queue) && !queue.IsEmpty)
        {
            var ids = queue.Keys.ToArray();
            foreach (var id in ids)
                queue.TryRemove(id, out _);

            await RunIndexingAsync(account, ids, false, cancellationToken).ConfigureAwait(false);
        }
    }

    private void CaptureSynchronizedMails(IReadOnlyList<MailCopy> mails, EntityUpdateSource source)
    {
        if (source != EntityUpdateSource.Server)
            return;

        foreach (var mail in mails)
        {
            if (mail.AssignedAccount is not { } account || RemoteMessageIdentity.TryCreate(mail) is not { } remoteMessageId)
                continue;

            _synchronizedMailQueues
                .GetOrAdd(account.Id, static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))[remoteMessageId] = 0;
        }
    }

    private IReadOnlyList<string> TakeSynchronizedMailIds(Guid accountId)
    {
        if (!_synchronizedMailQueues.TryGetValue(accountId, out var queue))
            return [];

        var ids = queue.Keys.ToArray();
        foreach (var id in ids)
            queue.TryRemove(id, out _);

        return ids;
    }

    private void ClearSynchronizedMails(Guid accountId)
    {
        if (_synchronizedMailQueues.TryGetValue(accountId, out var queue))
            queue.Clear();
    }

    private async Task<SemanticIndexAccountState> GetStateCoreAsync(
        Guid localMailAccountId,
        CancellationToken cancellationToken)
    {
        var account = await RequireAccountAsync(localMailAccountId).ConfigureAwait(false);
        var mailbox = await FindMailboxAsync(account, cancellationToken).ConfigureAwait(false);
        if (mailbox is null && account.Preferences.IsSemanticIndexingEnabled)
        {
            mailbox = await apiClient.EnsureSemanticMailboxAsync(
                account.Address,
                (int)account.ProviderType,
                cancellationToken).ConfigureAwait(false);
        }

        if (mailbox is null)
            return new(account.Preferences.IsSemanticIndexingEnabled, null, null, null, 0, false, false, false);

        var head = await apiClient.GetIntelligenceHeadAsync(mailbox.MailboxId, cancellationToken).ConfigureAwait(false);
        if (head is not null)
            await localStore.AlignMailboxHeadAsync(account.Id, head, cancellationToken).ConfigureAwait(false);
        var candidates = await messageResolver.GetCandidatesAsync(
            account.Id,
            cutoffUtc: null,
            cancellationToken).ConfigureAwait(false);
        var documents = await localStore.GetCurrentDocumentsAsync(
            account.Id,
            candidates.Select(static candidate => candidate.RemoteMessageId).ToArray(),
            cancellationToken).ConfigureAwait(false);
        var localState = await localStore.GetMailboxStateAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var localCoveredCount = documents.Count;
        var hasServerData = head is { IndexedMessageCount: > 0 } || head is { StorageSizeBytes: > 0 };
        var isUpToDate = head is not null && localState is not null &&
            localState.LastImportedRevision >= head.ArtifactRevision;

        return new(
            account.Preferences.IsSemanticIndexingEnabled,
            mailbox.MailboxId,
            head,
            localState,
            Math.Max(0, candidates.Count - localCoveredCount),
            candidates.Count > 0,
            isUpToDate,
            hasServerData && !isUpToDate,
            localCoveredCount);
    }

    private async Task<SemanticMailboxDto?> FindMailboxAsync(
        MailAccount account,
        CancellationToken cancellationToken)
    {
        var mailboxes = await apiClient.GetSemanticMailboxesAsync(cancellationToken).ConfigureAwait(false);
        return mailboxes.SingleOrDefault(mailbox =>
            mailbox.ProviderType == (int)account.ProviderType &&
            string.Equals(mailbox.Address.Trim(), account.Address.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task<SemanticMailboxDto> RequireMailboxAsync(
        MailAccount account,
        CancellationToken cancellationToken)
        => await FindMailboxAsync(account, cancellationToken).ConfigureAwait(false)
            ?? await apiClient.EnsureSemanticMailboxAsync(
                account.Address,
                (int)account.ProviderType,
                cancellationToken).ConfigureAwait(false);

    private async Task<MailboxContext> RequireMailboxHeadAsync(
        MailAccount account,
        CancellationToken cancellationToken)
    {
        var mailbox = await RequireMailboxAsync(account, cancellationToken).ConfigureAwait(false);
        var head = await apiClient.GetIntelligenceHeadAsync(mailbox.MailboxId, cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            await apiClient.BeginIntelligenceReindexAsync(
                mailbox.MailboxId,
                new BeginIntelligenceReindexRequest(WinoIntelligenceVersions.V1, Guid.NewGuid()),
                cancellationToken).ConfigureAwait(false);
            head = await apiClient.GetIntelligenceHeadAsync(mailbox.MailboxId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The intelligence mailbox head was not created.");
        }

        if (!string.Equals(head.IntelligenceVersion, WinoIntelligenceVersions.V1, StringComparison.Ordinal))
            throw new InvalidOperationException($"The intelligence version '{head.IntelligenceVersion}' is not supported by this client.");

        await localStore.AlignMailboxHeadAsync(account.Id, head, cancellationToken).ConfigureAwait(false);
        return new MailboxContext(mailbox, head);
    }

    private async Task SynchronizeChangesAsync(
        Guid localAccountId,
        MailboxContext mailbox,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var state = await localStore.GetMailboxStateAsync(localAccountId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The local intelligence mailbox state is not initialized.");
            var page = await apiClient.GetIntelligenceChangesAsync(
                mailbox.Mailbox.MailboxId,
                mailbox.Head.IntelligenceVersion,
                mailbox.Head.IndexEpoch,
                state.LastImportedRevision,
                250,
                cancellationToken).ConfigureAwait(false);
            await localStore.ApplyChangesAsync(
                localAccountId,
                mailbox.Mailbox.MailboxId,
                page,
                cancellationToken).ConfigureAwait(false);

            if (page.NextAfterRevision >= page.ThroughRevision)
                return;
            if (page.NextAfterRevision <= state.LastImportedRevision)
                throw new InvalidOperationException("The intelligence change cursor did not advance.");
        }
    }

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

    private SemanticMessageIndexState GetMessageState(Guid accountId, IntelligenceMessageCandidate candidate)
        => _messageStates.TryGetValue((accountId, candidate.RemoteMessageId), out var state)
            ? state
            : SemanticMessageIndexState.NotIndexed;

    private static DateTimeOffset ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(value),
        DateTimeKind.Local => new DateTimeOffset(value.ToUniversalTime()),
        _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
    };

    private void SetSnapshot(SemanticIndexJobSnapshot snapshot)
    {
        _snapshots[snapshot.LocalAccountId] = snapshot;
        messenger.Send(new SemanticIndexJobChanged(snapshot.LocalAccountId, snapshot));
    }

    private void ClearAccountState(Guid accountId)
    {
        _snapshots.TryRemove(accountId, out _);
        _automaticQueues.TryRemove(accountId, out _);
        _synchronizedMailQueues.TryRemove(accountId, out _);

        foreach (var key in _messageStates.Keys.Where(key => key.AccountId == accountId))
            _messageStates.TryRemove(key, out _);
    }

    private static string[] NormalizeIds(IEnumerable<string> remoteMessageIds)
        => remoteMessageIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private sealed record PreparedDocument(
        IntelligenceMessageCandidate Candidate,
        MessageIntelligenceSourceV1 Document);

    private sealed record MailboxContext(
        SemanticMailboxDto Mailbox,
        MailboxIntelligenceHeadDto Head);

    private sealed record ReconciliationRunResult(
        IReadOnlySet<string> CoveredRemoteMessageIds,
        int RestoredArtifactCount,
        int UploadedCount,
        int FailedCount);
}
