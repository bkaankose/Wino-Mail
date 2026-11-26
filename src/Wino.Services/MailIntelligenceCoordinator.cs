#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Intelligence.Keys;
using Wino.Core.Domain.Messaging.UI;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.ContentProcessing;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services.Extensions;

namespace Wino.Services;

/// <summary>
/// Owns the device side of mail intelligence. A run selects messages, splits them into batches
/// and sends each batch on the streamed route; every result is imported the moment its frame
/// arrives. The server keeps nothing, so a batch that does not finish simply leaves its messages
/// without artifacts, and the next run selects them again.
/// Nothing here runs for a user without the Wino Intelligence add-on.
/// </summary>
public sealed class MailIntelligenceCoordinator(
    IDatabaseService databaseService,
    IAccountService accountService,
    IWinoAccountApiClient apiClient,
    IMailIntelligenceStore store,
    ILocalIntelligenceService localIntelligenceService,
    MailIntelligenceEnvelopeBuilder envelopeBuilder,
    IIntelligenceResultKeyStore resultKeys,
    IntelligenceTransportKeyProvider transportKeys,
    ISemanticIndexJobRegistry jobRegistry,
    ITranslationService translationService,
    IIntelligenceMessageContextResolver messageResolver,
    IMessenger messenger,
    IWinoAccountIntelligenceSnapshotService? entitlementService = null,
    IApplicationConfiguration? applicationConfiguration = null)
    : IMailIntelligenceCoordinator, IAsyncDisposable
{
    private readonly MailIntelligenceDatasetWriter _datasetWriter = new(applicationConfiguration);

    /// <summary>
    /// The server allows two streams in flight per user, across every mailbox and device; a third
    /// is refused with 429. This bound is app-wide, not per run.
    /// </summary>
    private const int MaxConcurrentStreams = 2;

    private readonly SemaphoreSlim _streamSlots = new(MaxConcurrentStreams, MaxConcurrentStreams);

    /// <summary>Room left in a request for the JSON around the envelopes.</summary>
    private const int RequestFramingBytes = 64 * 1024;

    private const int MaxRateLimitRetries = 5;
    private static readonly TimeSpan DefaultRateLimitDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRateLimitDelay = TimeSpan.FromMinutes(1);

    /// <summary>
    /// After the allowance ran out, automatic submissions after sync wait this long before asking
    /// again. A manual run always asks.
    /// </summary>
    private static readonly TimeSpan QuotaBackoff = TimeSpan.FromHours(1);

    private long _quotaExhaustedUntilTicks;

    /// <summary>Progress is published at most this often; status changes always go out.</summary>
    private static readonly TimeSpan ProgressPublishInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Imported messages are announced in groups at most this often. Several presenters reload
    /// their whole state on each announcement.
    /// </summary>
    private static readonly TimeSpan MetadataFlushInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Preparing a message is CPU work (sanitizing and tokenizing the body) after one file read,
    /// so it scales with the cores the machine has.
    /// </summary>
    private static readonly int DocumentPreparationConcurrency = Math.Clamp(Environment.ProcessorCount, 4, 16);

    /// <summary>
    /// A v2 job older than this is gone on the server already (it expires a day after it ends),
    /// so its local row is dropped without asking.
    /// </summary>
    private static readonly TimeSpan LegacyJobAge = TimeSpan.FromDays(3);

    private readonly ConcurrentDictionary<Guid, MailIntelligenceJobSnapshot> _snapshots = new();

    /// <summary>
    /// Mails the server delivered since the last drain, per account and keyed by remote message
    /// id and folder. Drained after every mail synchronization; kept while the add-on cannot
    /// take work so the messages are not lost.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, OrderedDictionary<string, SynchronizedMailCapture>> _synchronizedQueues = new();

    /// <summary>
    /// Bound on a queue that cannot drain (no quota, signed out). The oldest capture gives way;
    /// a manual run still finds those messages because they never got an artifact.
    /// </summary>
    private const int MaxQueuedCapturesPerAccount = 10_000;
    private readonly ConcurrentDictionary<string, Task> _singleMessageRuns = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifecycle = new();
    private volatile bool _acceptingWork = true;
    private int _legacyCleanupRunning;
    private volatile bool _legacyCleanupDone;

    private static readonly Serilog.ILogger Logger = Serilog.Log.ForContext<MailIntelligenceCoordinator>();

    public Task InitializeAsync()
    {
        messenger.Register<AccountSynchronizationCompleted>(this, static (recipient, message) =>
            _ = ((MailIntelligenceCoordinator)recipient).HandleSynchronizationCompletedAsync(message));
        messenger.Register<MailAddedMessage>(this, static (recipient, message) =>
            ((MailIntelligenceCoordinator)recipient).CaptureSynchronizedMails([message.AddedMail], message.Source));
        messenger.Register<BulkMailAddedMessage>(this, static (recipient, message) =>
            ((MailIntelligenceCoordinator)recipient).CaptureSynchronizedMails(message.AddedMails, message.Source));
        messenger.Register<WinoAccountSignedInMessage>(this, static (recipient, _) =>
            ((MailIntelligenceCoordinator)recipient).ResumeWork());
        messenger.Register<AccountRemovedMessage>(this, static (recipient, message) =>
        {
            var coordinator = (MailIntelligenceCoordinator)recipient;
            coordinator._synchronizedQueues.TryRemove(message.Account.Id, out _);
            _ = coordinator.CancelAsync(message.Account.Id);
        });
        messenger.Register<WinoIntelligenceEntitlementChanged>(this, static (recipient, message) =>
        {
            var coordinator = (MailIntelligenceCoordinator)recipient;
            if (message.Entitlement.CanAccessSurfaces)
            {
                coordinator.StartLegacyCleanup();
            }

            if (message.Entitlement.CanConsumeQuota)
            {
                // Mails that arrived while the add-on could not take work were held back.
                coordinator.DrainAllSynchronizedQueues();
            }
        });

        if (entitlementService is null || entitlementService.CurrentEntitlement.CanAccessSurfaces)
        {
            StartLegacyCleanup();
        }

        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        StartLegacyCleanup();
        DrainAllSynchronizedQueues();
        return Task.CompletedTask;
    }

    public async Task CancelAllAsync(CancellationToken cancellationToken = default)
    {
        // Each run publishes Cancelled itself when its token fires.
        await jobRegistry.CancelAllAndWaitAsync().ConfigureAwait(false);
    }

    // ---- legacy v2 jobs ------------------------------------------------------------
    // Builds before the streamed route left server jobs (encrypted uploads and result pages in
    // Blob storage) and recorded them locally. Their messages never got artifacts, so the next
    // run selects them again; this only asks the server to delete the blobs now rather than at
    // expiry. Remove together with IWinoAccountApiClient.DeleteLegacyMailIntelligenceJobAsync
    // once no installed build can still hold v2 job rows.

    private void StartLegacyCleanup()
    {
        if (_legacyCleanupDone || _lifecycle.IsCancellationRequested ||
            Interlocked.CompareExchange(ref _legacyCleanupRunning, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await CleanUpLegacyJobsAsync(_lifecycle.Token).ConfigureAwait(false);
            }
            catch
            {
                // Best effort: the server expires these jobs on its own.
            }
            finally
            {
                Volatile.Write(ref _legacyCleanupRunning, 0);
            }
        });
    }

    private async Task CleanUpLegacyJobsAsync(CancellationToken cancellationToken)
    {
        var jobs = await store.GetLegacyJobsAsync(cancellationToken).ConfigureAwait(false);
        if (jobs.Count == 0)
        {
            _legacyCleanupDone = true;
            return;
        }

        var forgotten = new List<Guid>(jobs.Count);
        var cutoff = DateTime.UtcNow - LegacyJobAge;
        foreach (var job in jobs)
        {
            if (job.CreatedUtc < cutoff)
            {
                forgotten.Add(job.JobId);
                continue;
            }

            try
            {
                await apiClient.DeleteLegacyMailIntelligenceJobAsync(job.MailboxId, job.JobId, cancellationToken).ConfigureAwait(false);
                forgotten.Add(job.JobId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (WinoAccountApiException exception) when (
                exception.IsServiceUnavailable ||
                exception.ErrorCode == WinoAccountClientErrorCodes.SignInRequired ||
                exception.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Not answered: kept for the next launch, until it is old enough to drop.
            }
            catch
            {
                // Answered (already gone, expired, or refused): the server's expiry is the backstop.
                forgotten.Add(job.JobId);
            }
        }

        await store.DeleteLegacyJobsAsync(forgotten, cancellationToken).ConfigureAwait(false);
        Logger.Information("Removed {Removed} of {Count} intelligence jobs left by an earlier version.", forgotten.Count, jobs.Count);
        _legacyCleanupDone = forgotten.Count == jobs.Count;
    }

    // ---- runs ----------------------------------------------------------------------

    public async Task StartProcessingAsync(
        Guid localMailAccountId,
        IReadOnlyCollection<string> remoteMessageIds,
        CancellationToken cancellationToken = default)
    {
        ThrowIfWorkUnavailable();
        ArgumentNullException.ThrowIfNull(remoteMessageIds);
        if (remoteMessageIds.Count == 0)
        {
            return;
        }

        // A run already going for this account wins.
        jobRegistry.TryStart(
            localMailAccountId,
            token => RunAsync(localMailAccountId, remoteMessageIds, token),
            out _);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task RunAsync(
        Guid localMailAccountId,
        IReadOnlyCollection<string> remoteMessageIds,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifecycle.Token);
        cancellationToken = linked.Token;

        var run = new RunState(localMailAccountId, remoteMessageIds.Count);
        Publish(run, force: true);

        try
        {
            var account = await accountService.GetAccountAsync(localMailAccountId).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The account no longer exists.");
            var context = await RequireContextAsync(localMailAccountId, cancellationToken).ConfigureAwait(false);
            var resultKey = await RequireResultKeyAsync(context.WinoUserId, cancellationToken).ConfigureAwait(false);

            // Messages already processed at the current hash are not sent again.
            var alreadyProcessed = await store
                .GetProcessedMessageIdsAsync(localMailAccountId, remoteMessageIds, cancellationToken)
                .ConfigureAwait(false);

            var pending = remoteMessageIds
                .Where(id => !alreadyProcessed.Contains(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            run.SetSelected(pending.Length);
            if (pending.Length == 0)
            {
                run.Finish(MailIntelligenceJobStatus.Completed, null);
                return;
            }

            run.SetStatus(MailIntelligenceJobStatus.Uploading);
            Publish(run, force: true);

            // One query for the account, then a lookup per id.
            var byId = await GetCandidatesByIdAsync(account.Id, cancellationToken).ConfigureAwait(false);
            var batches = new BatchContext(account, context, resultKey, translationService.CurrentLanguageModel?.Code ?? "en-US", run);

            // Two chunks are worked on at once: one prepares while the other streams. The stream
            // slots bound the requests app-wide.
            await Parallel.ForEachAsync(
                pending.Chunk(MailIntelligenceStreamLimits.MaxMessagesPerBatch),
                new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentStreams, CancellationToken = cancellationToken },
                async (chunk, token) =>
                {
                    try
                    {
                        await ProcessChunkAsync(batches, byId, chunk, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        await StopRunAsync(batches, exception).ConfigureAwait(false);
                    }
                }).ConfigureAwait(false);

            FlushMetadata(run);
            run.Finish(run.FinalStatus, run.FinalErrorCode);
            Logger.Information(
                "Intelligence run for {AccountId}: {Selected} selected, {Processed} classified, {Failed} failures, ended {Status}",
                localMailAccountId, run.Selected, run.Processed, run.Failed, run.Status);
        }
        catch (OperationCanceledException)
        {
            FlushMetadata(run);
            run.Finish(MailIntelligenceJobStatus.Cancelled, null);
        }
        catch (Exception exception)
        {
            FlushMetadata(run);
            if (exception.Message == ApiErrorCodes.AiPackRequired)
            {
                _ = entitlementService?.RefreshEntitlementAsync();
            }

            run.Finish(MailIntelligenceJobStatus.Failed, exception.Message);
        }
        finally
        {
            Publish(run, force: true);
        }
    }

    private async Task ProcessChunkAsync(
        BatchContext batches,
        Dictionary<string, IntelligenceMessageCandidate> byId,
        string[] remoteMessageIds,
        CancellationToken cancellationToken)
    {
        var run = batches.Run;
        if (run.IsStopping)
        {
            return;
        }

        var candidates = new List<IntelligenceMessageCandidate>(remoteMessageIds.Length);
        foreach (var remoteMessageId in remoteMessageIds)
        {
            if (byId.TryGetValue(remoteMessageId, out var candidate))
            {
                candidates.Add(candidate);
            }
        }

        var prepared = candidates.Count == 0
            ? []
            : await PrepareAsync(batches.Account, candidates, cancellationToken).ConfigureAwait(false);

        // A message without a readable body is not sent and does not count toward the progress.
        run.Skip(remoteMessageIds.Length - prepared.Count);
        Publish(run);

        foreach (var batch in SplitBySize(prepared))
        {
            if (run.IsStopping)
            {
                return;
            }

            await SendBatchAsync(batches, batch, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Splits prepared messages so each request stays under the server's body limit. The size
    /// is estimated from the plaintext; the client checks the real body before sending.
    /// </summary>
    internal static List<List<MailIntelligenceUploadEnvelopeDto>> SplitBySize(IReadOnlyList<MailIntelligenceUploadEnvelopeDto> messages)
    {
        var batches = new List<List<MailIntelligenceUploadEnvelopeDto>>();
        var current = new List<MailIntelligenceUploadEnvelopeDto>();
        long currentBytes = 0;
        const long budget = MailIntelligenceStreamLimits.MaxRequestBytes - RequestFramingBytes;

        foreach (var message in messages)
        {
            var bytes = MailIntelligenceEnvelopeBuilder.EstimateRequestBytes(MailIntelligenceEnvelopeBuilder.MeasurePlaintext(message));
            if (current.Count > 0 &&
                (currentBytes + bytes > budget || current.Count >= MailIntelligenceStreamLimits.MaxMessagesPerBatch))
            {
                batches.Add(current);
                current = [];
                currentBytes = 0;
            }

            current.Add(message);
            currentBytes += bytes;
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    /// <summary>
    /// Sends one batch and imports its results as they stream in. Envelopes are encrypted only
    /// once a stream slot is held: the server refuses envelopes issued more than five minutes
    /// before the request.
    /// </summary>
    private async Task SendBatchAsync(
        BatchContext batches,
        List<MailIntelligenceUploadEnvelopeDto> batch,
        CancellationToken cancellationToken)
    {
        var run = batches.Run;
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in batch)
        {
            hashes.TryAdd(message.RemoteMessageId, message.ContentHash);
        }

        for (var attempt = 0; ; attempt++)
        {
            var delay = TimeSpan.Zero;
            var split = false;
            if (!_streamSlots.Wait(0))
            {
                run.SetStatus(MailIntelligenceJobStatus.Waiting);
                Publish(run, force: true);
                await _streamSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (run.IsStopping)
            {
                // Another batch stopped the run (quota, failure) while this one waited.
                _streamSlots.Release();
                return;
            }

            run.BatchStarted();
            Publish(run, force: true);
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var outcome = await WithTransportKeyAsync(async transportKey =>
                {
                    var batchId = Guid.NewGuid();
                    var envelopes = envelopeBuilder.BuildBatch(
                        batches.Context.WinoUserId, batches.Context.MailboxId, batch, transportKey);
                    try
                    {
                        var request = new MailIntelligenceStreamRequest(
                            batchId, batches.Language, batches.ResultKey.KeyId, batches.ResultKey.PublicKeyPem, envelopes);
                        return await MailIntelligenceStreamReader.ConsumeAsync(
                            apiClient.StreamMailIntelligenceAsync(batches.Context.MailboxId, request, cancellationToken),
                            batches.Context.MailboxId,
                            batchId,
                            (envelope, route, token) => resultKeys.DecryptAsync(
                                batches.ResultKey.KeyId, envelope, batches.Context.MailboxId, route, token),
                            (item, token) => ImportItemAsync(batches, hashes, item, token),
                            cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        MailIntelligenceEnvelopeBuilder.Clear(envelopes);
                    }
                }, cancellationToken).ConfigureAwait(false);

                Logger.Information(
                    "Intelligence batch of {Count} messages ended {End} after {Frames} results in {ElapsedMs} ms {ErrorCode}",
                    batch.Count, outcome.End, outcome.ResultFrames, clock.ElapsedMilliseconds, outcome.ErrorCode);

                if (outcome.End != MailIntelligenceStreamEnd.Done)
                {
                    // What arrived is imported; the rest has no artifact and is selected again
                    // by a later run.
                    run.NoteIncomplete(outcome.End switch
                    {
                        MailIntelligenceStreamEnd.Error => outcome.ErrorCode ?? ApiErrorCodes.IntelligenceGenerationRetryable,
                        MailIntelligenceStreamEnd.Refused => WinoAccountClientErrorCodes.InvalidServiceResponse,
                        _ => WinoAccountClientErrorCodes.ServiceUnavailable,
                    });
                }

                if (outcome.ErrorCode == ApiErrorCodes.AiQuotaExceeded)
                {
                    StopForQuota(run);
                }

                return;
            }
            catch (WinoAccountApiException exception) when (
                exception.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
            {
                delay = exception.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
                    ? (retryAfter < MaxRateLimitDelay ? retryAfter : MaxRateLimitDelay)
                    : DefaultRateLimitDelay;
            }
            catch (WinoAccountApiException exception) when (exception.ErrorCode == ApiErrorCodes.IntelligenceUploadTooLarge)
            {
                if (batch.Count == 1)
                {
                    Logger.Warning("A message is too large for intelligence and was skipped.");
                    run.Skip(1);
                    return;
                }

                split = true;
            }
            finally
            {
                run.BatchEnded();
                _streamSlots.Release();
                FlushMetadata(run);
                Publish(run, force: true);
            }

            if (split)
            {
                // Refused as too large: the halves go separately.
                var half = batch.Count / 2;
                await SendBatchAsync(batches, batch[..half], cancellationToken).ConfigureAwait(false);
                await SendBatchAsync(batches, batch[half..], cancellationToken).ConfigureAwait(false);
                return;
            }

            run.SetStatus(MailIntelligenceJobStatus.Waiting);
            Publish(run, force: true);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Imports one streamed item in its own transaction, so labels and priority show up while the
    /// rest of the batch is still being worked on, and enrichment fills in later.
    /// </summary>
    private async Task ImportItemAsync(
        BatchContext batches,
        IReadOnlyDictionary<string, string> hashes,
        MailIntelligenceStreamItemDto item,
        CancellationToken cancellationToken)
    {
        var run = batches.Run;
        var accountId = batches.Account.Id;
        switch (item.Kind)
        {
            case MailIntelligenceStreamItemKinds.Classification when item.Classification is { } classification:
            {
                var remoteMessageId = classification.Identity.RemoteMessageId;
                if (!hashes.TryGetValue(remoteMessageId, out var hash))
                {
                    return;
                }

                await store.ImportClassificationPageAsync(
                    accountId,
                    [MapClassification(classification)],
                    [],
                    new Dictionary<string, string>(StringComparer.Ordinal) { [remoteMessageId] = hash },
                    cancellationToken).ConfigureAwait(false);
                run.MessageClassified(remoteMessageId);
                break;
            }

            case MailIntelligenceStreamItemKinds.Enrichment when item.Enrichment is { } enrichment:
            {
                var remoteMessageId = enrichment.Identity.RemoteMessageId;
                if (!hashes.TryGetValue(remoteMessageId, out var hash))
                {
                    return;
                }

                await store.ImportEnrichmentPageAsync(
                    accountId,
                    [MapEnrichment(enrichment)],
                    [],
                    new Dictionary<string, string>(StringComparer.Ordinal) { [remoteMessageId] = hash },
                    cancellationToken).ConfigureAwait(false);
                run.MessageEnriched(remoteMessageId);
                break;
            }

            case MailIntelligenceStreamItemKinds.Failure when item.Failure is { } failure:
                run.MessageFailed();
                if (failure.ErrorCode == ApiErrorCodes.AiQuotaExceeded)
                {
                    // The batch was only partly paid for. Nothing more is sent in this run; the
                    // unpaid messages have no artifact and are selected again later.
                    StopForQuota(run);
                }

                break;

            default:
                // An item kind this build does not know.
                return;
        }

        if (run.ShouldFlushMetadata(MetadataFlushInterval))
        {
            FlushMetadata(run);
        }

        Publish(run);
    }

    private void StopForQuota(RunState run)
    {
        Interlocked.Exchange(ref _quotaExhaustedUntilTicks, (DateTime.UtcNow + QuotaBackoff).Ticks);
        if (run.Stop(MailIntelligenceJobStatus.PausedForQuota, Translator.Intelligence_QuotaExceeded))
        {
            // The fresh usage moves the entitlement to QuotaExhausted, which every surface reads.
            _ = entitlementService?.RefreshEntitlementAsync();
        }
    }

    /// <summary>
    /// A batch failed before or outside its stream. Nothing more is sent in this run; the reason
    /// decides how the run ends.
    /// </summary>
    private async Task StopRunAsync(BatchContext batches, Exception exception)
    {
        var run = batches.Run;
        switch (exception)
        {
            case WinoAccountApiException { ErrorCode: ApiErrorCodes.AiQuotaExceeded }:
                StopForQuota(run);
                return;

            case IntelligenceResultKeyLostException lost:
                if (run.Stop(MailIntelligenceJobStatus.Failed, Translator.Intelligence_ResultKeyLost))
                {
                    await HandleLostResultKeyAsync(lost.KeyId).ConfigureAwait(false);
                }

                return;

            default:
                if (exception.Message == ApiErrorCodes.AiPackRequired)
                {
                    // The server disagrees with the local snapshot. Ask again and let the fresh
                    // answer drive the key lifecycle; one refusal never removes anything by itself.
                    _ = entitlementService?.RefreshEntitlementAsync();
                }

                Logger.Warning("An intelligence batch failed: {ErrorCode}", exception.Message);
                run.Stop(MailIntelligenceJobStatus.Failed, exception.Message);
                return;
        }
    }

    /// <summary>
    /// The device can no longer open results encrypted to its key. Retrying never helps: the dead
    /// key is dropped and a fresh key is made while the add-on is active. The messages still lack
    /// artifacts, so they are offered again.
    /// </summary>
    private async Task HandleLostResultKeyAsync(string keyId)
    {
        try
        {
            await resultKeys.DeleteAsync(keyId).ConfigureAwait(false);
            if (entitlementService?.CurrentEntitlement is { State: WinoIntelligenceEntitlementState.Active, WinoAccountId: { } winoUserId })
            {
                await resultKeys.GetOrCreateAsync(winoUserId).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Logger.Warning(exception, "Replacing the lost intelligence result key failed.");
        }
    }

    /// <summary>
    /// Runs one request against the current transport key. When the server no longer knows that
    /// key (it rotated), the key is fetched again and the request rebuilt once.
    /// </summary>
    private async Task<T> WithTransportKeyAsync<T>(
        Func<Mail.AI.Cryptography.ContentEncryptionPublicKey, Task<T>> send,
        CancellationToken cancellationToken)
    {
        var transportKey = await transportKeys.GetAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await send(transportKey).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (exception.Message == ApiErrorCodes.IntelligenceEnvelopeKeyUnknown)
        {
            transportKeys.Invalidate();
            transportKey = await transportKeys.GetAsync(cancellationToken).ConfigureAwait(false);
            return await send(transportKey).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads every body in one pass (local MIME first, then the provider in as few round trips as
    /// it allows), then projects them in parallel. The projection is CPU work, so it scales with
    /// cores; the body reads are bounded by what the provider tolerates.
    /// </summary>
    private async Task<List<MailIntelligenceUploadEnvelopeDto>> PrepareAsync(
        MailAccount account,
        IReadOnlyList<IntelligenceMessageCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var contents = await messageResolver.GetContentsAsync(account.Id, candidates, cancellationToken).ConfigureAwait(false);
        var readMs = clock.ElapsedMilliseconds;

        var results = new MailIntelligenceUploadEnvelopeDto?[candidates.Count];
        await Parallel.ForAsync(0, candidates.Count, new ParallelOptions
        {
            MaxDegreeOfParallelism = DocumentPreparationConcurrency,
            CancellationToken = cancellationToken,
        }, async (index, token) =>
        {
            var candidate = candidates[index];
            if (!contents.TryGetValue(candidate.RemoteMessageId, out var content))
            {
                // A message whose body cannot be read is skipped rather than failing the run.
                return;
            }

            try
            {
                results[index] = await PrepareOneAsync(account, candidate, content, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                results[index] = null;
            }
        }).ConfigureAwait(false);

        Logger.Information(
            "Prepared {Prepared}/{Count} messages: bodies {ReadMs} ms, projection {ProjectMs} ms",
            results.Count(static item => item is not null), candidates.Count, readMs, clock.ElapsedMilliseconds - readMs);
        return [.. results.OfType<MailIntelligenceUploadEnvelopeDto>()];
    }

    private async Task<MailIntelligenceUploadEnvelopeDto> PrepareOneAsync(
        MailAccount account,
        IntelligenceMessageCandidate candidate,
        CancellationToken cancellationToken)
        => await PrepareOneAsync(
            account,
            candidate,
            await messageResolver.GetContentAsync(account.Id, candidate, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    private Task<MailIntelligenceUploadEnvelopeDto> PrepareOneAsync(
        MailAccount account,
        IntelligenceMessageCandidate candidate,
        SemanticMailContent content,
        CancellationToken cancellationToken)
    {
        var from = content.From.Count > 0
            ? content.From
            : [new MailAddress(candidate.Sender, candidate.SenderName)];

        // Bound the upload content before deriving its identity. Inline image data carries no
        // text and would only push large bodies past the sanitizer's input limit.
        var body = content.Body.Format == MailBodyFormat.Html
            ? content.Body with { Content = MailContentProjectorExtensions.RemoveEmbeddedImageData(content.Body.Content) }
            : content.Body;
        var processed = new MailContentProcessor(new HtmlContentSanitizer())
            .Prepare(from, candidate.Subject, body, ContentProfile);

        var envelope = new MailIntelligenceUploadEnvelopeDto
        {
            RemoteMessageId = candidate.RemoteMessageId,
            ContentHash = processed.ContentHash,
            Subject = processed.Subject,
            Sender = processed.From,
            Body = processed.Body,
            OccurredAtUtc = ToUtc(candidate.ReceivedAt),
            IsOutgoing = candidate.IsOutgoing,
            IsDirectRecipient = content.ToRecipients.Any(address =>
                string.Equals(address, account.Address, StringComparison.OrdinalIgnoreCase)),
            HasLaterOutgoingReply = candidate.HasLaterOutgoingReply,
            ProviderImportance = candidate.ProviderImportance,
            RemoteFolderIds = candidate.RemoteFolderIds,
            HasListUnsubscribe = content.HasListUnsubscribe,
        };

        _datasetWriter.Write(account.Id, account.Address, candidate, content, envelope);
        return Task.FromResult(envelope);
    }

    private async Task<Dictionary<string, IntelligenceMessageCandidate>> GetCandidatesByIdAsync(
        Guid localAccountId, CancellationToken cancellationToken)
    {
        var candidates = await messageResolver.GetCandidatesAsync(localAccountId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var byId = new Dictionary<string, IntelligenceMessageCandidate>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            byId.TryAdd(candidate.RemoteMessageId, candidate);
        }

        return byId;
    }

    private static readonly MailContentProfile ContentProfile = MailContentProfile.Default;

    internal static ClassificationArtifact MapClassification(MailClassificationArtifactDto item) => new(
        new MailArtifactKey(item.Identity.RemoteMessageId, item.Identity.ContentHash),
        [.. item.Labels.Select(static label => label.ToString().ToLowerInvariant())],
        item.Priority.ToString().ToLowerInvariant(),
        [.. (item.Hints ?? []).Select(SmartActionKindIds.Get)],
        item.IncludeInBriefing,
        item.CompletedUtc.UtcDateTime);

    internal static EnrichmentArtifact MapEnrichment(MailEnrichmentArtifactDto item) => new(
        new MailArtifactKey(item.Identity.RemoteMessageId, item.Identity.ContentHash),
        item.Headline ?? string.Empty,
        item.Summary ?? string.Empty,
        item.Actions ?? [],
        item.CompletedUtc.UtcDateTime);

    // ---- single message ------------------------------------------------------------

    public async Task ProcessMessageAsync(
        Guid localMailAccountId, string mailUniqueId, CancellationToken cancellationToken = default)
    {
        ThrowIfWorkUnavailable();
        var key = $"{localMailAccountId:D}|{mailUniqueId}";

        // Repeated clicks join the in-flight run instead of paying for the work twice.
        var run = _singleMessageRuns.GetOrAdd(key, _ => ProcessSingleAsync(localMailAccountId, mailUniqueId, cancellationToken));
        try
        {
            await run.ConfigureAwait(false);
        }
        finally
        {
            _singleMessageRuns.TryRemove(key, out _);
        }
    }

    private async Task ProcessSingleAsync(Guid localMailAccountId, string mailUniqueId, CancellationToken cancellationToken)
    {
        var account = await accountService.GetAccountAsync(localMailAccountId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account no longer exists.");
        var context = await RequireContextAsync(localMailAccountId, cancellationToken).ConfigureAwait(false);

        var candidate = await messageResolver.FindCandidateAsync(localMailAccountId, mailUniqueId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The message is not available for processing.");

        var projection = await PrepareOneAsync(account, candidate, cancellationToken).ConfigureAwait(false);
        var language = translationService.CurrentLanguageModel?.Code ?? "en-US";

        // The single-message route returns its artifacts in the response and stores nothing on
        // the server, so it needs the add-on and the transport key but no result key.
        var response = await WithTransportKeyAsync(async transportKey =>
        {
            var envelope = envelopeBuilder.BuildSingle(context.WinoUserId, context.MailboxId, projection, transportKey);
            try
            {
                return await apiClient
                    .AnalyzeMailAsync(context.MailboxId, envelope, language, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope);
            }
        }, cancellationToken).ConfigureAwait(false);

        var desired = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [projection.RemoteMessageId] = projection.ContentHash,
        };

        // Both artifacts are imported before the caller's UI action completes.
        await store.ImportClassificationPageAsync(
            localMailAccountId,
            [MapClassification(response.Classification)],
            [],
            desired,
            cancellationToken).ConfigureAwait(false);

        if (response.Enrichment is { } enrichment)
        {
            await store.ImportEnrichmentPageAsync(
                localMailAccountId,
                [MapEnrichment(enrichment)],
                [],
                desired,
                cancellationToken).ConfigureAwait(false);
        }

        messenger.Send(new IntelligenceMetadataChanged(
            localMailAccountId, new HashSet<string>(StringComparer.Ordinal) { projection.RemoteMessageId }, IntelligenceMetadataChangeScope.Messages));
    }

    // ---- run control and state -----------------------------------------------------

    public async Task CancelAsync(Guid localMailAccountId, CancellationToken cancellationToken = default)
    {
        var wasRunning = jobRegistry.IsRunning(localMailAccountId);
        await jobRegistry.CancelAndWaitAsync(localMailAccountId).ConfigureAwait(false);
        if (!wasRunning)
        {
            SetSnapshot(GetJobSnapshot(localMailAccountId) with { Status = MailIntelligenceJobStatus.Cancelled, ActiveBatchCount = 0 });
        }
    }

    public MailIntelligenceJobSnapshot GetJobSnapshot(Guid localMailAccountId)
        => _snapshots.TryGetValue(localMailAccountId, out var snapshot)
            ? snapshot
            : MailIntelligenceJobSnapshot.Idle(localMailAccountId);

    public async Task<MailIntelligenceAccountState> GetStateAsync(
        Guid localMailAccountId, CancellationToken cancellationToken = default)
    {
        var account = await accountService.GetAccountAsync(localMailAccountId).ConfigureAwait(false);
        if (account is null)
        {
            return MailIntelligenceAccountState.Empty;
        }

        var access = await store.GetAccessAsync(localMailAccountId, cancellationToken).ConfigureAwait(false);
        var candidates = await messageResolver.GetCandidatesAsync(localMailAccountId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var remoteIds = candidates.Select(static candidate => candidate.RemoteMessageId).ToArray();
        var processed = await store.GetProcessedMessageIdsAsync(localMailAccountId, remoteIds, cancellationToken).ConfigureAwait(false);

        return new MailIntelligenceAccountState(
            account.Preferences?.IsSemanticIndexingEnabled == true,
            access is { MailboxId: var mailboxId } && mailboxId != Guid.Empty ? mailboxId : null,
            processed.Count,
            Math.Max(0, remoteIds.Length - processed.Count),
            remoteIds.Length > 0,
            jobRegistry.IsRunning(localMailAccountId) ? Math.Max(1, GetJobSnapshot(localMailAccountId).ActiveBatchCount) : 0);
    }

    public async Task<MailMessageIntelligenceState> GetMessageStateAsync(
        Guid localMailAccountId, string mailUniqueId, CancellationToken cancellationToken = default)
    {
        var candidate = await messageResolver
            .FindCandidateAsync(localMailAccountId, mailUniqueId, cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null)
        {
            return MailMessageIntelligenceState.Unsupported;
        }

        var artifacts = await store
            .GetClassificationArtifactsAsync(localMailAccountId, [candidate.RemoteMessageId], cancellationToken)
            .ConfigureAwait(false);
        if (artifacts.ContainsKey(candidate.RemoteMessageId))
        {
            return MailMessageIntelligenceState.Processed;
        }

        return _singleMessageRuns.ContainsKey($"{localMailAccountId:D}|{mailUniqueId}")
            ? MailMessageIntelligenceState.Processing
            : MailMessageIntelligenceState.NotProcessed;
    }

    public async Task DeleteLocalIntelligenceAsync(Guid localMailAccountId, CancellationToken cancellationToken = default)
    {
        await store.DeleteAccountAsync(localMailAccountId, cancellationToken).ConfigureAwait(false);
        messenger.Send(new IntelligenceMetadataChanged(
            localMailAccountId, new HashSet<string>(StringComparer.Ordinal), IntelligenceMetadataChangeScope.MailboxReset));
    }

    /// <summary>
    /// The only path that removes imported artifacts. Runs are stopped and jobs an earlier build
    /// left on the server are deleted while a token may still exist, then the whole intelligence
    /// database goes, keys included.
    /// </summary>
    public async Task ResetLocalStateAsync(CancellationToken cancellationToken = default)
    {
        _acceptingWork = false;
        try
        {
            await CancelAllAsync(cancellationToken).ConfigureAwait(false);
            await CleanUpLegacyJobsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The server expires legacy jobs on its own; the local wipe must not depend on it.
        }

        await store.DeleteDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await resultKeys.DeleteAllAsync(cancellationToken).ConfigureAwait(false);
        _snapshots.Clear();
    }

    // ---- automatic sync ------------------------------------------------------------

    private void CaptureSynchronizedMails(IEnumerable<Core.Domain.Entities.Mail.MailCopy> mails, Core.Domain.Enums.EntityUpdateSource source)
    {
        if (source != Core.Domain.Enums.EntityUpdateSource.Server)
        {
            return;
        }

        foreach (var mail in mails)
        {
            var accountId = mail.AssignedAccount?.Id;
            var capture = AutomaticIndexingSelection.TryCapture(mail);
            if (accountId is null || capture is null)
            {
                continue;
            }

            var queue = _synchronizedQueues.GetOrAdd(accountId.Value, static _ => new OrderedDictionary<string, SynchronizedMailCapture>(StringComparer.Ordinal));
            lock (queue)
            {
                Enqueue(queue, capture);
            }
        }
    }

    /// <summary>
    /// Adds a capture under the queue's lock. Every folder copy of a message is kept, because
    /// which folders it sits in decides whether it is included; the selection dedupes by
    /// message. The oldest capture gives way at the bound.
    /// </summary>
    private static void Enqueue(OrderedDictionary<string, SynchronizedMailCapture> queue, SynchronizedMailCapture capture)
    {
        if (!queue.TryAdd($"{capture.RemoteMessageId}\u001f{capture.RemoteFolderId}", capture))
        {
            return;
        }

        while (queue.Count > MaxQueuedCapturesPerAccount)
        {
            queue.RemoveAt(0);
        }
    }

    private Task HandleSynchronizationCompletedAsync(AccountSynchronizationCompleted message)
        => DrainSynchronizedQueueAsync(message.AccountId);

    private void DrainAllSynchronizedQueues()
    {
        foreach (var accountId in _synchronizedQueues.Keys)
        {
            _ = DrainSynchronizedQueueAsync(accountId);
        }
    }

    /// <summary>
    /// Sends the captured mails of one account as a run. When a run for the account is already
    /// going (a manual run, or the previous automatic one), the captures are put back and sent
    /// once it ends. When the add-on cannot take work they stay queued for the next drain. Only
    /// the user turning the setting off discards them.
    /// </summary>
    private async Task DrainSynchronizedQueueAsync(Guid accountId)
    {
        try
        {
            while (!_lifecycle.IsCancellationRequested)
            {
                if (!_synchronizedQueues.TryGetValue(accountId, out var queue))
                {
                    return;
                }

                if (!await CanSubmitAutomaticallyAsync(_lifecycle.Token).ConfigureAwait(false))
                {
                    return;
                }

                if (!await localIntelligenceService.ShouldAutomaticallyProcessAsync(accountId, _lifecycle.Token).ConfigureAwait(false))
                {
                    _synchronizedQueues.TryRemove(accountId, out _);
                    return;
                }

                SynchronizedMailCapture[] captured;
                lock (queue)
                {
                    captured = [.. queue.Values];
                    queue.Clear();
                }

                if (captured.Length == 0)
                {
                    return;
                }

                var account = await accountService.GetAccountAsync(accountId).ConfigureAwait(false);
                if (account is null)
                {
                    _synchronizedQueues.TryRemove(accountId, out _);
                    return;
                }

                var remoteMessageIds = AutomaticIndexingSelection.Select(account.Preferences, captured);
                if (remoteMessageIds.Count == 0)
                {
                    // Nothing landed in an included folder. Loop once more in case captures
                    // arrived meanwhile; an empty queue ends it.
                    continue;
                }

                if (jobRegistry.TryStart(
                        accountId,
                        token => RunAsync(accountId, remoteMessageIds, token),
                        out var running))
                {
                    return;
                }

                lock (queue)
                {
                    foreach (var capture in captured)
                    {
                        Enqueue(queue, capture);
                    }
                }

                await running.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Intelligence must never break mail synchronization. The captures that were taken
            // are gone; the next manual run still finds the messages without artifacts.
        }
    }

    /// <summary>
    /// Whether an automatic submission can be made right now. Unlike a manual run this never
    /// throws: a queue that cannot drain simply waits for the next trigger.
    /// </summary>
    private async Task<bool> CanSubmitAutomaticallyAsync(CancellationToken cancellationToken)
    {
        if (!_acceptingWork || DateTime.UtcNow.Ticks < Interlocked.Read(ref _quotaExhaustedUntilTicks))
        {
            return false;
        }

        if (entitlementService is null)
        {
            return true;
        }

        var entitlement = entitlementService.CurrentEntitlement;
        if (!entitlement.CanConsumeQuota || entitlement.WinoAccountId is not { } winoAccountId)
        {
            return false;
        }

        // Consent is checked from the cached account snapshot, the same source the reading pane
        // uses. Without a snapshot the server decides; its refusal refreshes the entitlement.
        var snapshot = await entitlementService.GetCachedAsync(winoAccountId, cancellationToken).ConfigureAwait(false);
        return snapshot is null ||
               snapshot.Consent is { } consent &&
               consent.Status == ConsentStatuses.Active &&
               consent.AcceptedPolicyVersion == consent.CurrentPolicyVersion;
    }

    private void ResumeWork()
    {
        _acceptingWork = true;
        DrainAllSynchronizedQueues();
    }

    // ---- helpers -------------------------------------------------------------------

    private sealed record MailIntelligenceContext(Guid WinoUserId, Guid MailboxId);

    private sealed record BatchContext(
        MailAccount Account,
        MailIntelligenceContext Context,
        IntelligenceResultKey ResultKey,
        string Language,
        RunState Run);

    /// <summary>
    /// Resolves the Wino user and the mailbox id that scopes this account's requests on the
    /// server. A new mailbox uses its local account id; accounts that already stored a synced
    /// mailbox id keep it.
    /// </summary>
    private async Task<MailIntelligenceContext> RequireContextAsync(Guid localAccountId, CancellationToken cancellationToken)
    {
        var winoAccount = await databaseService.Connection.Table<Core.Domain.Entities.Shared.WinoAccount>()
            .FirstOrDefaultAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("A Wino account is required for mail intelligence.");

        var access = await store.GetAccessAsync(localAccountId, cancellationToken).ConfigureAwait(false);
        if (access is { MailboxId: var mailboxId } && mailboxId != Guid.Empty)
        {
            return new MailIntelligenceContext(winoAccount.Id, mailboxId);
        }

        await store.SaveAccessAsync(localAccountId, winoAccount.Id, localAccountId, true, true, cancellationToken).ConfigureAwait(false);
        return new MailIntelligenceContext(winoAccount.Id, localAccountId);
    }

    /// <summary>
    /// The key the results will be encrypted to. Created here only when the add-on is active and
    /// the lifecycle has not made one yet; there are no plaintext results.
    /// </summary>
    private async Task<IntelligenceResultKey> RequireResultKeyAsync(Guid winoUserId, CancellationToken cancellationToken)
    {
        try
        {
            var key = await resultKeys.GetActiveKeyAsync(winoUserId, cancellationToken).ConfigureAwait(false);
            if (key is not null)
            {
                return key;
            }

            if (entitlementService is null || entitlementService.CurrentEntitlement.CanConsumeQuota)
            {
                return await resultKeys.GetOrCreateAsync(winoUserId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(Translator.Intelligence_ResultKeyUnavailable, exception);
        }

        throw new InvalidOperationException(Translator.Intelligence_ResultKeyUnavailable);
    }

    private void ThrowIfWorkUnavailable()
    {
        if (!_acceptingWork)
        {
            throw new InvalidOperationException("Mail intelligence is not accepting work.");
        }

        if (entitlementService is not null && !entitlementService.CurrentEntitlement.CanConsumeQuota)
        {
            throw new InvalidOperationException(
                $"Mail intelligence is unavailable: {entitlementService.CurrentEntitlement.State}.");
        }
    }

    private void Publish(RunState run, bool force = false)
    {
        if (run.TryTakeSnapshot(force, ProgressPublishInterval) is { } snapshot)
        {
            SetSnapshot(snapshot);
        }
    }

    private void SetSnapshot(MailIntelligenceJobSnapshot snapshot)
    {
        _snapshots[snapshot.LocalAccountId] = snapshot;
        messenger.Send(new MailIntelligenceJobChanged(snapshot.LocalAccountId, snapshot));
    }

    private void FlushMetadata(RunState run)
    {
        var changed = run.TakeChangedMessages();
        if (changed.Count > 0)
        {
            messenger.Send(new IntelligenceMetadataChanged(run.AccountId, changed, IntelligenceMetadataChangeScope.Messages));
        }
    }

    private static DateTimeOffset ToUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? new DateTimeOffset(value, TimeSpan.Zero)
            : new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero);

    public async ValueTask DisposeAsync()
    {
        _acceptingWork = false;
        messenger.UnregisterAll(this);
        await _lifecycle.CancelAsync().ConfigureAwait(false);

        try
        {
            await jobRegistry.CancelAllAndWaitAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifecycle.Dispose();
    }

    /// <summary>
    /// The in-memory state of one account's run: counts for the progress card, why it stopped, and
    /// the messages whose artifacts changed since the last announcement.
    /// </summary>
    private sealed class RunState(Guid accountId, int selected)
    {
        private readonly Lock _gate = new();
        private HashSet<string> _changed = new(StringComparer.Ordinal);
        private long _lastPublishedTicks;
        private long _lastFlushTicks = DateTime.UtcNow.Ticks;
        private bool _dirty = true;
        private MailIntelligenceJobStatus? _stopStatus;
        private string? _stopCode;
        private string? _incompleteCode;

        public Guid AccountId { get; } = accountId;
        public int Selected { get; private set; } = selected;
        public int Processed { get; private set; }
        public int Failed { get; private set; }
        public int Active { get; private set; }
        public MailIntelligenceJobStatus Status { get; private set; } = MailIntelligenceJobStatus.Calculating;
        public string? ErrorCode { get; private set; }

        public bool IsStopping
        {
            get
            {
                lock (_gate)
                {
                    return _stopStatus is not null;
                }
            }
        }

        public MailIntelligenceJobStatus FinalStatus
        {
            get
            {
                lock (_gate)
                {
                    return _stopStatus ?? (_incompleteCode is null ? MailIntelligenceJobStatus.Completed : MailIntelligenceJobStatus.Failed);
                }
            }
        }

        public string? FinalErrorCode
        {
            get
            {
                lock (_gate)
                {
                    return _stopStatus is not null ? _stopCode : _incompleteCode;
                }
            }
        }

        public void SetSelected(int count) => Mutate(() => Selected = count);

        public void Skip(int count)
        {
            if (count > 0)
            {
                Mutate(() => Selected = Math.Max(0, Selected - count));
            }
        }

        public void SetStatus(MailIntelligenceJobStatus status) => Mutate(() =>
        {
            // While another batch of the run streams, the run reads as receiving, not waiting.
            if (Status is not (MailIntelligenceJobStatus.Completed or MailIntelligenceJobStatus.Failed or
                    MailIntelligenceJobStatus.Cancelled or MailIntelligenceJobStatus.PausedForQuota) &&
                !(status == MailIntelligenceJobStatus.Waiting && Active > 0))
            {
                Status = status;
            }
        });

        public void BatchStarted() => Mutate(() =>
        {
            Active++;
            Status = MailIntelligenceJobStatus.Downloading;
        });

        public void BatchEnded() => Mutate(() =>
        {
            Active = Math.Max(0, Active - 1);
            if (Active == 0 && _stopStatus is null)
            {
                Status = MailIntelligenceJobStatus.Uploading;
            }
        });

        public void MessageClassified(string remoteMessageId) => Mutate(() =>
        {
            Processed++;
            _changed.Add(remoteMessageId);
        });

        public void MessageEnriched(string remoteMessageId) => Mutate(() => _changed.Add(remoteMessageId));

        public void MessageFailed() => Mutate(() => Failed++);

        public void NoteIncomplete(string errorCode) => Mutate(() => _incompleteCode = errorCode);

        /// <summary>Asks the run to send nothing more. Returns false when it was already stopping.</summary>
        public bool Stop(MailIntelligenceJobStatus status, string? errorCode)
        {
            lock (_gate)
            {
                if (_stopStatus is not null)
                {
                    return false;
                }

                _stopStatus = status;
                _stopCode = errorCode;
                _dirty = true;
                return true;
            }
        }

        public void Finish(MailIntelligenceJobStatus status, string? errorCode) => Mutate(() =>
        {
            Status = status;
            ErrorCode = errorCode;
            Active = 0;
        });

        public bool ShouldFlushMetadata(TimeSpan interval)
        {
            lock (_gate)
            {
                return _changed.Count > 0 && DateTime.UtcNow.Ticks - _lastFlushTicks >= interval.Ticks;
            }
        }

        public HashSet<string> TakeChangedMessages()
        {
            lock (_gate)
            {
                var changed = _changed;
                _changed = new HashSet<string>(StringComparer.Ordinal);
                _lastFlushTicks = DateTime.UtcNow.Ticks;
                return changed;
            }
        }

        public MailIntelligenceJobSnapshot? TryTakeSnapshot(bool force, TimeSpan interval)
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow.Ticks;
                if (!force && (!_dirty || now - _lastPublishedTicks < interval.Ticks))
                {
                    return null;
                }

                _dirty = false;
                _lastPublishedTicks = now;
                return new MailIntelligenceJobSnapshot(AccountId, Status, Selected, Processed, Failed, Active, ErrorCode);
            }
        }

        private void Mutate(Action change)
        {
            lock (_gate)
            {
                change();
                _dirty = true;
            }
        }
    }
}
