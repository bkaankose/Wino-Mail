#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Intelligence.Keys;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.ContentProcessing;
using Wino.Mail.Contracts.Intelligence;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Server;
using Wino.Messaging.UI;

namespace Wino.Services;

/// <summary>
/// Owns the device side of mail intelligence.
/// Replaces the revision change feed: work is submitted as jobs, each job's two stages are
/// downloaded and imported independently, and a stage is acknowledged only after its
/// import transaction has committed.
/// Every job is bound to the device result key its results are encrypted to. Nothing here runs
/// for a user without the Wino Intelligence add-on: no polling, no key store, no endpoints.
/// </summary>
public sealed class MailIntelligenceCoordinator(
    IDatabaseService databaseService,
    IAccountService accountService,
    IWinoAccountApiClient apiClient,
    IMailIntelligenceStore store,
    ILocalIntelligenceService localIntelligenceService,
    MailIntelligenceUploadBuilder uploadBuilder,
    IIntelligenceResultKeyStore resultKeys,
    IntelligenceTransportKeyProvider transportKeys,
    MailIntelligenceResultPageReader pageReader,
    ISemanticIndexJobRegistry jobRegistry,
    ITranslationService translationService,
    IIntelligenceMessageContextResolver messageResolver,
    IMessenger messenger,
    IWinoAccountIntelligenceSnapshotService? entitlementService = null)
    : IMailIntelligenceCoordinator, IAsyncDisposable
{
    /// <summary>
    /// Server cap per job. A larger selection is split so no single upload is rejected.
    /// </summary>
    private const int MaxMessagesPerJob = 1_000;

    /// <summary>
    /// Preparing a message is CPU work (sanitizing and tokenizing the body) after one file read,
    /// so it scales with the cores the machine has.
    /// </summary>
    private static readonly int DocumentPreparationConcurrency = Math.Clamp(Environment.ProcessorCount, 4, 16);

    private readonly ConcurrentDictionary<Guid, MailIntelligenceJobSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<Guid, List<string>> _synchronizedQueues = new();
    private readonly ConcurrentDictionary<string, Task> _singleMessageRuns = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifecycle = new();
    private readonly Lock _resumeLoopGate = new();
    private Task? _resumeLoop;
    private volatile bool _acceptingWork = true;

    private static readonly Serilog.ILogger Logger = Serilog.Log.ForContext<MailIntelligenceCoordinator>();

    /// <summary>
    /// How long one status request may wait on the server for a stage to become downloadable.
    /// The server answers the moment one is, so this only bounds an idle wait.
    /// </summary>
    private const int StatusWaitSeconds = 25;

    /// <summary>
    /// Pause after a round that came back at once with nothing to do, for example from a server
    /// that ignores the wait. Keeps the loop from spinning.
    /// </summary>
    private static readonly TimeSpan IdleRoundDelay = TimeSpan.FromSeconds(3);

    /// <summary>Pause when another poller holds a job, before asking again.</summary>
    private static readonly TimeSpan BusyJobDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>The long-poll follower of each unfinished job.</summary>
    private readonly ConcurrentDictionary<Guid, Task> _jobFollowers = new();

    /// <summary>One poller per job at a time, so two paths never import the same stage twice.</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _jobGates = new();

    /// <summary>
    /// Content hashes computed when messages were prepared for upload. Import compares every
    /// artifact against the current hash; within a job's lifetime the prepared hash is current,
    /// so it saves re-reading and re-projecting every body.
    /// </summary>
    private readonly ConcurrentDictionary<(Guid AccountId, string RemoteMessageId), (string Hash, DateTime PreparedUtc)> _preparedHashes = new();

    private static readonly TimeSpan PreparedHashLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A server job this device does not track is only swept once it is well past the server's
    /// own 24 hour expiry, so an in-flight job from another device is never touched.
    /// </summary>
    private static readonly TimeSpan OrphanAge = TimeSpan.FromDays(3);

    private int _orphanSweepStarted;

    /// <summary>
    /// Jobs are followed only while the add-on is usable. Quota exhaustion still downloads what
    /// was already paid for; an unknown or ended entitlement follows nothing and deletes nothing.
    /// </summary>
    private bool CanFollowJobs => entitlementService is null || entitlementService.CurrentEntitlement.CanAccessSurfaces;

    public Task InitializeAsync()
    {
        // The content projection loads its tokenizer on first use, which takes seconds. Doing
        // that now keeps it off the first indexing run of the session.
        _ = Task.Run(WarmContentProcessor);

        messenger.Register<AccountSynchronizationCompleted>(this, static (recipient, message) =>
            _ = ((MailIntelligenceCoordinator)recipient).HandleSynchronizationCompletedAsync(message));
        messenger.Register<MailAddedMessage>(this, static (recipient, message) =>
            ((MailIntelligenceCoordinator)recipient).CaptureSynchronizedMails([message.AddedMail], message.Source));
        messenger.Register<BulkMailAddedMessage>(this, static (recipient, message) =>
            ((MailIntelligenceCoordinator)recipient).CaptureSynchronizedMails(message.AddedMails, message.Source));
        messenger.Register<WinoAccountSignedInMessage>(this, static (recipient, _) =>
            ((MailIntelligenceCoordinator)recipient).ResumeWork());
        messenger.Register<WinoIntelligenceEntitlementChanged>(this, static (recipient, message) =>
        {
            if (message.Entitlement.CanAccessSurfaces)
            {
                ((MailIntelligenceCoordinator)recipient).EnsureResumeLoopRunning();
            }
        });

        // A job submitted in an earlier session is still waiting on the server. Nothing
        // else will ask about it: the only other poll happens at the end of a submission,
        // so without this a quiet mailbox never collects its results, never acknowledges
        // the stages, and the server therefore never deletes the job's blobs.
        // A user without the add-on never gets here; the entitlement message starts it later.
        if (CanFollowJobs)
        {
            EnsureResumeLoopRunning();
        }

        return Task.CompletedTask;
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        EnsureResumeLoopRunning();

        if (Interlocked.Exchange(ref _orphanSweepStarted, 1) == 0)
        {
            await SweepOrphanedJobsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Deletes server jobs this device does not track, such as those left behind by a wiped
    /// install. Best effort: the server's own expiry is the backstop.
    /// TODO: filter by resultKeyId once Contracts 3.0.0-alpha.1 carries it on the job list.
    /// </summary>
    private async Task SweepOrphanedJobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var remote = await apiClient.GetMailIntelligenceJobsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var cutoff = DateTimeOffset.UtcNow - OrphanAge;
            foreach (var job in remote.Jobs)
            {
                if (job.CreatedUtc > cutoff ||
                    await store.GetJobAsync(job.JobId, cancellationToken).ConfigureAwait(false) is not null)
                {
                    continue;
                }

                await apiClient.CancelMailIntelligenceJobAsync(job.MailboxId, job.JobId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The server expires these on its own.
        }
    }

    public async Task AbandonJobsAsync(CancellationToken cancellationToken = default)
    {
        await jobRegistry.CancelAllAndWaitAsync().ConfigureAwait(false);
        var jobs = await store.GetUnfinishedJobsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in jobs)
        {
            await CancelJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
            SetSnapshot(job.LocalAccountId, snapshot => snapshot with { Status = MailIntelligenceJobStatus.Cancelled });
        }
    }

    /// <summary>
    /// Starts a follower for every unfinished job that does not have one. Runs at launch, when the
    /// add-on becomes usable, and after every submission, so a job outlives the session that
    /// created it and a new job is followed the moment it exists.
    /// </summary>
    private void EnsureResumeLoopRunning()
    {
        if (_lifecycle.IsCancellationRequested)
        {
            return;
        }

        lock (_resumeLoopGate)
        {
            _resumeLoop = Task.Run(() => StartJobFollowersAsync(_lifecycle.Token));
        }
    }

    private async Task StartJobFollowersAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!CanFollowJobs)
            {
                // Restarted by the next entitlement change that makes jobs usable again.
                return;
            }

            foreach (var job in await store.GetUnfinishedJobsAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = _jobFollowers.GetOrAdd(job.JobId, jobId => Task.Run(() => FollowJobAsync(jobId, cancellationToken)));
            }
        }
        catch
        {
            // Intelligence must never break the app. The next trigger starts them again.
        }
    }

    /// <summary>
    /// Follows one job to the end with long polls: each request returns the moment a stage can be
    /// downloaded, so results are collected as soon as the server publishes them.
    /// </summary>
    private async Task FollowJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && CanFollowJobs)
            {
                var job = await store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
                if (job is null)
                {
                    return;
                }

                var started = DateTime.UtcNow;
                var polled = await TryPollJobAsync(job, StatusWaitSeconds, cancellationToken).ConfigureAwait(false);
                if (!polled)
                {
                    // Another poller holds the job for the moment.
                    await Task.Delay(BusyJobDelay, cancellationToken).ConfigureAwait(false);
                }
                else if (DateTime.UtcNow - started < TimeSpan.FromSeconds(1))
                {
                    // Answered at once with nothing new: a server that does not hold requests.
                    var latest = await store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
                    if (latest is not null && latest with { UpdatedUtc = job.UpdatedUtc } == job)
                    {
                        await Task.Delay(IdleRoundDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Intelligence must never break the app. The next trigger starts a new follower.
        }
        finally
        {
            _jobFollowers.TryRemove(jobId, out _);
        }
    }

    // ---- submission ----------------------------------------------------------------

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

        if (!jobRegistry.TryStart(
                localMailAccountId,
                token => RunSubmissionAsync(localMailAccountId, remoteMessageIds, token),
                out _))
        {
            // A submission is already running for this account.
            return;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task RunSubmissionAsync(
        Guid localMailAccountId,
        IReadOnlyCollection<string> remoteMessageIds,
        CancellationToken cancellationToken)
    {
        SetSnapshot(localMailAccountId, snapshot => snapshot with
        {
            Status = MailIntelligenceJobStatus.Calculating,
            SelectedMessageCount = remoteMessageIds.Count,
        });

        try
        {
            var account = await accountService.GetAccountAsync(localMailAccountId).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The account no longer exists.");
            var context = await RequireContextAsync(localMailAccountId, cancellationToken).ConfigureAwait(false);
            var resultKey = await RequireResultKeyAsync(context.WinoUserId, cancellationToken).ConfigureAwait(false);

            // Messages already processed at the current hash are not resubmitted.
            var alreadyProcessed = await store
                .GetProcessedMessageIdsAsync(localMailAccountId, remoteMessageIds, cancellationToken)
                .ConfigureAwait(false);

            var pending = remoteMessageIds.Where(id => !alreadyProcessed.Contains(id)).ToArray();
            if (pending.Length == 0)
            {
                SetSnapshot(localMailAccountId, snapshot => snapshot with { Status = MailIntelligenceJobStatus.Completed });
                return;
            }

            SetSnapshot(localMailAccountId, snapshot => snapshot with { Status = MailIntelligenceJobStatus.Uploading });

            foreach (var chunk in pending.Chunk(MaxMessagesPerJob))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SubmitJobAsync(account, context, resultKey, chunk, cancellationToken).ConfigureAwait(false);
            }

            SetSnapshot(localMailAccountId, snapshot => snapshot with { Status = MailIntelligenceJobStatus.Waiting });

            // Each new job gets a follower that long-polls it, so results are collected the
            // moment they are published, and still arrive if the app restarts in between.
            EnsureResumeLoopRunning();
        }
        catch (OperationCanceledException)
        {
            SetSnapshot(localMailAccountId, snapshot => snapshot with { Status = MailIntelligenceJobStatus.Cancelled });
        }
        catch (Exception exception)
        {
            if (exception.Message == ApiErrorCodes.AiPackRequired)
            {
                // The server disagrees with the local snapshot. Ask again and let the fresh
                // answer drive the key lifecycle; one refusal never removes anything by itself.
                _ = entitlementService?.RefreshEntitlementAsync();
            }

            SetSnapshot(localMailAccountId, snapshot => snapshot with
            {
                Status = MailIntelligenceJobStatus.Failed,
                ErrorCode = exception.Message,
            });
        }
    }

    private async Task SubmitJobAsync(
        MailAccount account,
        MailIntelligenceContext context,
        IntelligenceResultKey resultKey,
        IReadOnlyList<string> remoteMessageIds,
        CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // One query for the account, then a lookup per id. Finding each id separately re-read
        // every candidate of the account once per message.
        var byId = await GetCandidatesByIdAsync(account.Id, cancellationToken).ConfigureAwait(false);
        var candidatesMs = clock.ElapsedMilliseconds;
        var candidates = new List<IntelligenceMessageCandidate>(remoteMessageIds.Count);
        foreach (var remoteMessageId in remoteMessageIds)
        {
            if (byId.TryGetValue(remoteMessageId, out var candidate))
            {
                candidates.Add(candidate);
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var prepared = await PrepareAsync(account, candidates, cancellationToken).ConfigureAwait(false);
        var preparedMs = clock.ElapsedMilliseconds;
        if (prepared.Count == 0)
        {
            return;
        }

        var language = translationService.CurrentLanguageModel?.Code ?? "en-US";
        long builtMs = 0;
        var accepted = await WithTransportKeyAsync(async transportKey =>
        {
            var upload = uploadBuilder.Build(
                context.WinoUserId, context.MailboxId, Guid.NewGuid(), prepared, language, transportKey, resultKey);
            builtMs = clock.ElapsedMilliseconds;
            try
            {
                return await apiClient.SubmitMailIntelligenceJobAsync(
                    context.MailboxId, upload.JobId, upload.Sha256, upload.Content, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(upload.Content);
            }
        }, cancellationToken).ConfigureAwait(false);

        Logger.Information(
            "Intelligence job {JobId}: {Count} messages. Candidates {CandidatesMs} ms, prepare {PrepareMs} ms, build {BuildMs} ms, upload {UploadMs} ms",
            accepted.JobId, prepared.Count, candidatesMs, preparedMs - candidatesMs, builtMs - preparedMs, clock.ElapsedMilliseconds - builtMs);

        // The job id is persisted before anything else, so a crash right after upload
        // still leaves the device able to collect the results.
        await store.UpsertJobAsync(new MailIntelligenceJobState(
            accepted.JobId,
            account.Id,
            context.MailboxId,
            prepared.Count,
            MailIntelligenceJobStatuses.Pending,
            new MailIntelligenceStageState(MailIntelligenceStageStatuses.Pending, 0, null, false, false),
            new MailIntelligenceStageState(MailIntelligenceStageStatuses.Pending, 0, null, false, false),
            0,
            null,
            DateTime.UtcNow,
            DateTime.UtcNow)
        {
            ResultKeyId = resultKey.KeyId,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one upload against the current transport key. When the server no longer knows that
    /// key (it rotated), the key is fetched again and the upload rebuilt once.
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
                // A message whose body cannot be read is skipped rather than failing the job.
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

        // The content profile pins the canonical projection and the hash. It has nothing
        // to do with vectors any more.
        var processed = new MailContentProcessor(new HtmlContentSanitizer())
            .Prepare(from, candidate.Subject, content.Body, ContentProfile);

        _preparedHashes[(account.Id, candidate.RemoteMessageId)] = (processed.ContentHash, DateTime.UtcNow);

        return Task.FromResult(new MailIntelligenceUploadEnvelopeDto
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
        });
    }

    private static void WarmContentProcessor()
    {
        try
        {
            new MailContentProcessor(new HtmlContentSanitizer()).Prepare(
                [new MailAddress("warmup@wino.invalid", "Wino")],
                "Warm-up",
                new MailBodyContent(MailBodyFormat.PlainText, "Warm-up"),
                ContentProfile);
        }
        catch
        {
            // Only a warm-up; the real call reports its own failure.
        }
    }

    // ---- polling and import --------------------------------------------------------

    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        if (!_acceptingWork || !CanFollowJobs)
        {
            return;
        }

        var jobs = await store.GetUnfinishedJobsAsync(cancellationToken).ConfigureAwait(false);
        await PollJobsAsync(jobs, 0, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Polls the jobs side by side. A job another poller is already handling is skipped.</summary>
    private Task PollJobsAsync(IReadOnlyList<MailIntelligenceJobState> jobs, int waitSeconds, CancellationToken cancellationToken)
    {
        if (!_acceptingWork || !CanFollowJobs)
        {
            return Task.CompletedTask;
        }

        return Task.WhenAll(jobs.Select(job => TryPollJobAsync(job, waitSeconds, cancellationToken)));
    }

    /// <summary>Polls one job unless another poller holds it. Returns whether it polled.</summary>
    private async Task<bool> TryPollJobAsync(MailIntelligenceJobState job, int waitSeconds, CancellationToken cancellationToken)
    {
        var gate = _jobGates.GetOrAdd(job.JobId, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        try
        {
            // Re-read under the gate: the previous holder may have finished the job.
            var current = await store.GetJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
            if (current is not null)
            {
                await PollJobAsync(current, waitSeconds, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetSnapshot(job.LocalAccountId, snapshot => snapshot with { ErrorCode = exception.Message });
        }
        finally
        {
            gate.Release();
        }

        return true;
    }

    private async Task PollJobAsync(MailIntelligenceJobState job, int waitSeconds, CancellationToken cancellationToken)
    {
        if (job.ResultKeyId is { } resultKeyId &&
            !(await resultKeys.GetKeysAsync(cancellationToken).ConfigureAwait(false)).Any(key => key.KeyId == resultKeyId))
        {
            // Encrypted to a key this device no longer holds: unreadable by design, so it is
            // deleted rather than polled again.
            await CancelJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
            return;
        }

        MailIntelligenceJobDto? remote;
        try
        {
            remote = await apiClient
                .GetMailIntelligenceJobAsync(job.MailboxId, job.JobId, waitSeconds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (exception.Message == ApiErrorCodes.IntelligenceJobExpired)
        {
            // Unacknowledged for too long, so the server deleted it. The messages still lack
            // artifacts and are picked up again by the next processing run.
            await store.DeleteJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (remote is null)
        {
            // The server deleted it, which only happens after both stages were acknowledged.
            await store.DeleteJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (remote.Status == MailIntelligenceJobStatuses.Expired)
        {
            // Same as a 410: the results are gone and the messages are picked up again.
            await store.DeleteJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var updated = job with
        {
            Status = remote.Status,
            Classification = job.Classification with { Status = remote.Classification.Status, PageCount = remote.Classification.PageCount, Digest = remote.Classification.ResultDigest },
            Enrichment = job.Enrichment with { Status = remote.Enrichment.Status, PageCount = remote.Enrichment.PageCount, Digest = remote.Enrichment.ResultDigest },
            FailedCount = remote.Classification.FailedCount + remote.Enrichment.FailedCount,
        };
        await store.UpsertJobAsync(updated, cancellationToken).ConfigureAwait(false);

        SetSnapshot(job.LocalAccountId, snapshot => snapshot with
        {
            Status = MailIntelligenceJobStatus.Downloading,
            Classification = new MailIntelligenceStageProgress(remote.Classification.Status, remote.Classification.PageCount, updated.Classification.IsImported, remote.Classification.IsAcknowledged),
            Enrichment = new MailIntelligenceStageProgress(remote.Enrichment.Status, remote.Enrichment.PageCount, updated.Enrichment.IsImported, remote.Enrichment.IsAcknowledged),
            FailedMessageCount = updated.FailedCount,
        });

        try
        {
            // Classification is published first and imported first, so labels and priority appear before
            // any headline exists.
            if (remote.Classification.Status == MailIntelligenceStageStatuses.Published && !updated.Classification.IsAcknowledged)
            {
                await ImportClassificationStageAsync(updated, remote, cancellationToken).ConfigureAwait(false);
            }

            if (remote.Enrichment.Status == MailIntelligenceStageStatuses.Published && !updated.Enrichment.IsAcknowledged)
            {
                await ImportEnrichmentStageAsync(updated, remote, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IntelligenceResultKeyLostException exception)
        {
            await HandleLostResultKeyAsync(job, exception.KeyId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var latest = await store.GetJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
        if (latest is not null && latest.IsFinished)
        {
            await store.DeleteJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
            SetSnapshot(job.LocalAccountId, snapshot => snapshot with { Status = MailIntelligenceJobStatus.Completed });
            messenger.Send(new IntelligenceMetadataChanged(
            job.LocalAccountId, new HashSet<string>(StringComparer.Ordinal), IntelligenceMetadataChangeScope.Messages));
        }
    }

    private async Task ImportClassificationStageAsync(
        MailIntelligenceJobState job, MailIntelligenceJobDto remote, CancellationToken cancellationToken)
    {
        var imported = 0;
        var pageHashes = new List<string?>();
        for (var page = 0; page < remote.Classification.PageCount; page++)
        {
            var read = await pageReader.ReadClassificationAsync(job, page, cancellationToken).ConfigureAwait(false);
            pageHashes.Add(read.EnvelopeHash);
            var dto = read.Page;

            var artifacts = dto.Items.Select(MapClassification).ToArray();

            var desired = await ResolveDesiredHashesAsync(
                job.LocalAccountId, artifacts.Select(static x => x.Key.RemoteMessageId), cancellationToken).ConfigureAwait(false);

            var result = await store.ImportClassificationPageAsync(
                job.LocalAccountId, artifacts, MapFailures(dto.Failures), desired, cancellationToken).ConfigureAwait(false);
            imported += result.Imported;
        }

        // The import has committed, so the stage can be acknowledged.
        await store.MarkStageImportedAsync(job.JobId, MailIntelligenceStageKind.Classification, cancellationToken).ConfigureAwait(false);
        EnsureDigestMatches(job, MailIntelligenceStageIds.Classification, pageHashes, remote.Classification.ResultDigest);
        await apiClient.AcknowledgeMailIntelligenceStageAsync(
            job.MailboxId, job.JobId, "classification", remote.Classification.ResultDigest ?? string.Empty, cancellationToken).ConfigureAwait(false);
        await store.MarkStageAcknowledgedAsync(job.JobId, MailIntelligenceStageKind.Classification, cancellationToken).ConfigureAwait(false);

        SetSnapshot(job.LocalAccountId, snapshot => snapshot with
        {
            ProcessedMessageCount = snapshot.ProcessedMessageCount + imported,
        });
        messenger.Send(new IntelligenceMetadataChanged(
            job.LocalAccountId, new HashSet<string>(StringComparer.Ordinal), IntelligenceMetadataChangeScope.Messages));
    }

    /// <summary>
    /// For an encrypted job, checks that the pages read are the set the server published before
    /// the stage is acknowledged. Each envelope is already authenticated on decrypt; this catches
    /// a missing, repeated or reordered page. A mismatch leaves the stage unacknowledged.
    /// </summary>
    internal static void EnsureDigestMatches(
        MailIntelligenceJobState job, string stage, IReadOnlyList<string?> pageHashes, string? serverDigest)
    {
        if (job.ResultKeyId is null)
        {
            return;
        }

        var digest = MailIntelligenceResultDigest.Compute(pageHashes.Select(static hash =>
            hash ?? throw new InvalidOperationException("An encrypted result page had no envelope hash.")));
        if (!string.Equals(digest, serverDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The {stage} results did not match the server's digest and were not acknowledged.");
        }
    }

    private async Task ImportEnrichmentStageAsync(
        MailIntelligenceJobState job, MailIntelligenceJobDto remote, CancellationToken cancellationToken)
    {
        var pageHashes = new List<string?>();
        for (var page = 0; page < remote.Enrichment.PageCount; page++)
        {
            var read = await pageReader.ReadEnrichmentAsync(job, page, cancellationToken).ConfigureAwait(false);
            pageHashes.Add(read.EnvelopeHash);
            var dto = read.Page;

            var artifacts = dto.Items.Select(MapEnrichment).ToArray();

            var desired = await ResolveDesiredHashesAsync(
                job.LocalAccountId, artifacts.Select(static x => x.Key.RemoteMessageId), cancellationToken).ConfigureAwait(false);

            await store.ImportEnrichmentPageAsync(
                job.LocalAccountId, artifacts, MapFailures(dto.Failures), desired, cancellationToken).ConfigureAwait(false);
        }

        await store.MarkStageImportedAsync(job.JobId, MailIntelligenceStageKind.Enrichment, cancellationToken).ConfigureAwait(false);
        EnsureDigestMatches(job, MailIntelligenceStageIds.Enrichment, pageHashes, remote.Enrichment.ResultDigest);
        await apiClient.AcknowledgeMailIntelligenceStageAsync(
            job.MailboxId, job.JobId, MailIntelligenceStageIds.Enrichment, remote.Enrichment.ResultDigest ?? string.Empty, cancellationToken).ConfigureAwait(false);
        await store.MarkStageAcknowledgedAsync(job.JobId, MailIntelligenceStageKind.Enrichment, cancellationToken).ConfigureAwait(false);

        messenger.Send(new IntelligenceMetadataChanged(
            job.LocalAccountId, new HashSet<string>(StringComparer.Ordinal), IntelligenceMetadataChangeScope.Messages));
    }

    /// <summary>
    /// The device can no longer open this job's results. Retrying never helps: the job is
    /// deleted here and on the server, the dead key is dropped, and a fresh key is made while
    /// the add-on is active. The messages still lack artifacts, so they are offered again.
    /// </summary>
    private async Task HandleLostResultKeyAsync(MailIntelligenceJobState job, string keyId, CancellationToken cancellationToken)
    {
        await CancelJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
        await resultKeys.DeleteAsync(keyId, cancellationToken).ConfigureAwait(false);

        if (entitlementService?.CurrentEntitlement is { State: WinoIntelligenceEntitlementState.Active, WinoAccountId: { } winoUserId })
        {
            await resultKeys.GetOrCreateAsync(winoUserId, cancellationToken).ConfigureAwait(false);
        }

        SetSnapshot(job.LocalAccountId, snapshot => snapshot with
        {
            Status = MailIntelligenceJobStatus.Failed,
            ErrorCode = Translator.Intelligence_ResultKeyLost,
        });
    }

    /// <summary>
    /// The hash each message currently has locally. An artifact carrying a different hash
    /// describes content the user no longer has, so the store drops it.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ResolveDesiredHashesAsync(
        Guid localAccountId, IEnumerable<string> remoteMessageIds, CancellationToken cancellationToken)
    {
        var account = await accountService.GetAccountAsync(localAccountId).ConfigureAwait(false);
        if (account is null)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var hashes = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        var now = DateTime.UtcNow;
        foreach (var remoteMessageId in remoteMessageIds.Distinct(StringComparer.Ordinal))
        {
            if (_preparedHashes.TryGetValue((localAccountId, remoteMessageId), out var prepared) &&
                now - prepared.PreparedUtc < PreparedHashLifetime)
            {
                hashes[remoteMessageId] = prepared.Hash;
            }
            else
            {
                missing.Add(remoteMessageId);
            }
        }

        if (missing.Count > 0)
        {
            var byId = await GetCandidatesByIdAsync(localAccountId, cancellationToken).ConfigureAwait(false);
            using var gate = new SemaphoreSlim(DocumentPreparationConcurrency, DocumentPreparationConcurrency);
            await Task.WhenAll(missing.Select(async remoteMessageId =>
            {
                if (!byId.TryGetValue(remoteMessageId, out var candidate))
                {
                    return;
                }

                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var projection = await PrepareOneAsync(account, candidate, cancellationToken).ConfigureAwait(false);
                    hashes[remoteMessageId] = projection.ContentHash;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Without a readable body the local hash is unknown, so the artifact is
                    // accepted rather than discarded on a guess.
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
        }

        return new Dictionary<string, string>(hashes, StringComparer.Ordinal);
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

    private static IReadOnlyList<MailIntelligenceItemFailure> MapFailures(IReadOnlyList<MailIntelligenceFailureDto> failures)
        => [.. failures.Select(failure => new MailIntelligenceItemFailure(
            new MailArtifactKey(failure.Identity.RemoteMessageId, failure.Identity.ContentHash),
            string.Equals(failure.Stage, MailIntelligenceStageIds.Enrichment, StringComparison.Ordinal)
                ? MailIntelligenceStageKind.Enrichment
                : MailIntelligenceStageKind.Classification,
            failure.ErrorCode))];

    /// <summary>
    /// Tokenizer, token limit and hash version for the upload projection. The published
    /// package only exposes the old embedding profile type, so the values the app used before
    /// (o200k_base, 12,000 tokens, content version 1) are pinned here. Model and dimensions
    /// are unused by the processor.
    /// </summary>
    private static readonly EmbeddingProfile ContentProfile =
        new("wino-intelligence-content-v1", "none", 768, 1, "o200k_base", 12_000);

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
            var envelope = uploadBuilder.BuildSingle(context.WinoUserId, context.MailboxId, projection, transportKey);
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

    // ---- job control ---------------------------------------------------------------

    public async Task CancelAsync(Guid localMailAccountId, CancellationToken cancellationToken = default)
    {
        await jobRegistry.CancelAndWaitAsync(localMailAccountId).ConfigureAwait(false);
        var jobs = await store.GetJobsForAccountAsync(localMailAccountId, cancellationToken).ConfigureAwait(false);
        foreach (var job in jobs)
        {
            await CancelJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
        }

        SetSnapshot(localMailAccountId, snapshot => snapshot with { Status = MailIntelligenceJobStatus.Cancelled });
    }

    public async Task CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return;
        }

        try
        {
            await apiClient.CancelMailIntelligenceJobAsync(job.MailboxId, jobId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The local record is removed either way; the server deletes its own blobs on
            // consent revocation if the cancel never lands.
        }

        await store.DeleteJobAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-polls one job. A failed item is retried by submitting a new job, not here.</summary>
    public async Task RetryJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await store.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is not null)
        {
            await PollJobAsync(job, 0, cancellationToken).ConfigureAwait(false);
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
        var jobs = await store.GetJobsForAccountAsync(localMailAccountId, cancellationToken).ConfigureAwait(false);
        var candidates = await messageResolver.GetCandidatesAsync(localMailAccountId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var remoteIds = candidates.Select(static candidate => candidate.RemoteMessageId).ToArray();
        var processed = await store.GetProcessedMessageIdsAsync(localMailAccountId, remoteIds, cancellationToken).ConfigureAwait(false);

        return new MailIntelligenceAccountState(
            account.Preferences?.IsSemanticIndexingEnabled == true,
            access is { MailboxId: var mailboxId } && mailboxId != Guid.Empty ? mailboxId : null,
            processed.Count,
            Math.Max(0, remoteIds.Length - processed.Count),
            remoteIds.Length > 0,
            jobs.Count(static job => !job.IsFinished));
    }

    public async Task<IReadOnlyList<MailIntelligenceJobState>> GetJobsAsync(
        Guid localMailAccountId, CancellationToken cancellationToken = default)
        => await store.GetJobsForAccountAsync(localMailAccountId, cancellationToken).ConfigureAwait(false);

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
    /// The only path that removes imported artifacts. Jobs are deleted on the server while a
    /// token may still exist, then the whole intelligence database goes, keys included.
    /// </summary>
    public async Task ResetLocalStateAsync(CancellationToken cancellationToken = default)
    {
        _acceptingWork = false;
        try
        {
            await AbandonJobsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The server expires them on its own; the local wipe must not depend on it.
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
            var remoteMessageId = RemoteMessageIdentity.TryCreate(mail);
            if (accountId is null || remoteMessageId is null)
            {
                continue;
            }

            var queue = _synchronizedQueues.GetOrAdd(accountId.Value, static _ => []);
            lock (queue)
            {
                queue.Add(remoteMessageId);
            }
        }
    }

    private async Task HandleSynchronizationCompletedAsync(AccountSynchronizationCompleted message)
    {
        try
        {
            if (!_acceptingWork ||
                !_synchronizedQueues.TryRemove(message.AccountId, out var queue))
            {
                return;
            }

            string[] pending;
            lock (queue)
            {
                pending = [.. queue];
                queue.Clear();
            }

            if (pending.Length == 0 ||
                !await localIntelligenceService.ShouldAutomaticallyProcessAsync(message.AccountId).ConfigureAwait(false))
            {
                return;
            }

            // Newly discovered messages become their own job, so several jobs for one
            // mailbox can be in flight at once.
            await StartProcessingAsync(message.AccountId, pending, _lifecycle.Token).ConfigureAwait(false);
        }
        catch
        {
            // Intelligence must never break mail synchronization.
        }
    }

    private void ResumeWork() => _acceptingWork = true;

    // ---- helpers -------------------------------------------------------------------

    private sealed record MailIntelligenceContext(Guid WinoUserId, Guid MailboxId);

    /// <summary>
    /// Resolves the Wino user and the server mailbox id. The mailbox id comes from the
    /// existing account sync, so intelligence keeps no registry of its own.
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

        // The id is only available once account/mailbox sync has run, so that has to
        // complete before any intelligence work can be submitted.
        var mailboxes = await apiClient.GetMailboxesAsync(cancellationToken).ConfigureAwait(false);
        var account = await accountService.GetAccountAsync(localAccountId).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account no longer exists.");

        var match = mailboxes.Mailboxes.FirstOrDefault(mailbox =>
            string.Equals(mailbox.Address?.Trim(), account.Address?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            mailbox.MailboxId is not null);

        if (match?.MailboxId is not { } resolved)
        {
            throw new InvalidOperationException(
                "This mailbox has not finished account synchronization yet, so intelligence cannot be submitted.");
        }

        await store.SaveAccessAsync(localAccountId, winoAccount.Id, resolved, true, true, cancellationToken).ConfigureAwait(false);
        return new MailIntelligenceContext(winoAccount.Id, resolved);
    }

    /// <summary>
    /// The key the job's results will be encrypted to. Created here only when the add-on is
    /// active and the lifecycle has not made one yet; never falls back to plaintext results.
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

    private void SetSnapshot(Guid localAccountId, Func<MailIntelligenceJobSnapshot, MailIntelligenceJobSnapshot> mutate)
    {
        var snapshot = mutate(GetJobSnapshot(localAccountId));
        _snapshots[localAccountId] = snapshot;
        messenger.Send(new MailIntelligenceJobChanged(localAccountId, snapshot));
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

        var loops = _jobFollowers.Values.ToList();
        if (_resumeLoop is { } loop)
        {
            loops.Add(loop);
        }

        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifecycle.Dispose();
    }
}
