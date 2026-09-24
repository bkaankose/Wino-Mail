#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
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
/// </summary>
public sealed class MailIntelligenceCoordinator(
    IDatabaseService databaseService,
    IAccountService accountService,
    IWinoAccountApiClient apiClient,
    IMailIntelligenceStore store,
    ILocalIntelligenceService localIntelligenceService,
    MailIntelligenceUploadBuilder uploadBuilder,
    ISemanticIndexJobRegistry jobRegistry,
    ITranslationService translationService,
    IIntelligenceMessageContextResolver messageResolver,
    IMessenger messenger,
    IWinoIntelligenceEntitlementService? entitlementService = null)
    : IMailIntelligenceCoordinator, IAsyncDisposable
{
    /// <summary>
    /// Server cap per job. A larger selection is split so no single upload is rejected.
    /// </summary>
    private const int MaxMessagesPerJob = 1_000;

    private const int DocumentPreparationConcurrency = 4;

    private readonly ConcurrentDictionary<Guid, MailIntelligenceJobSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<Guid, List<string>> _synchronizedQueues = new();
    private readonly ConcurrentDictionary<string, Task> _singleMessageRuns = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifecycle = new();
    private readonly Lock _resumeLoopGate = new();
    private Task? _resumeLoop;
    private volatile bool _acceptingWork = true;

    /// <summary>How often an unfinished job is re-checked while the app is running.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

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

        // A job submitted in an earlier session is still waiting on the server. Nothing
        // else will ask about it: the only other poll happens at the end of a submission,
        // so without this a quiet mailbox never collects its results, never acknowledges
        // the stages, and the server therefore never deletes the job's blobs.
        _resumeLoop = Task.Run(() => ResumePendingJobsAsync(_lifecycle.Token));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Polls unfinished jobs until none remain. Runs at launch and whenever a submission
    /// adds work, so a job outlives the session that created it.
    /// </summary>
    private async Task ResumePendingJobsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var jobs = await store.GetUnfinishedJobsAsync(cancellationToken).ConfigureAwait(false);
                if (jobs.Count == 0)
                {
                    return;
                }

                await PollAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Intelligence must never break the app. The next tick tries again.
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Restarts the poll loop if it has finished because nothing was outstanding. Called
    /// after a submission so newly created jobs are followed to completion.
    /// </summary>
    private void EnsureResumeLoopRunning()
    {
        if (_lifecycle.IsCancellationRequested)
        {
            return;
        }

        lock (_resumeLoopGate)
        {
            if (_resumeLoop is { IsCompleted: false })
            {
                return;
            }

            _resumeLoop = Task.Run(() => ResumePendingJobsAsync(_lifecycle.Token));
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
                await SubmitJobAsync(account, context, chunk, cancellationToken).ConfigureAwait(false);
            }

            SetSnapshot(localMailAccountId, snapshot => snapshot with { Status = MailIntelligenceJobStatus.Waiting });
            await PollAsync(cancellationToken).ConfigureAwait(false);

            // Whatever this poll did not finish is followed by the resume loop, so results
            // arrive even if the app is closed and reopened before the server is done.
            EnsureResumeLoopRunning();
        }
        catch (OperationCanceledException)
        {
            SetSnapshot(localMailAccountId, snapshot => snapshot with { Status = MailIntelligenceJobStatus.Cancelled });
        }
        catch (Exception exception)
        {
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
        IReadOnlyList<string> remoteMessageIds,
        CancellationToken cancellationToken)
    {
        var candidates = new List<IntelligenceMessageCandidate>();
        foreach (var remoteMessageId in remoteMessageIds)
        {
            var candidate = await messageResolver
                .FindCandidateAsync(account.Id, remoteMessageId, cancellationToken)
                .ConfigureAwait(false);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var prepared = await PrepareAsync(account, candidates, cancellationToken).ConfigureAwait(false);
        if (prepared.Count == 0)
        {
            return;
        }

        var jobId = Guid.NewGuid();
        var language = translationService.CurrentLanguageModel?.Code ?? "en-US";
        var upload = uploadBuilder.Build(context.WinoUserId, context.MailboxId, jobId, prepared, language);

        try
        {
            var accepted = await apiClient.SubmitMailIntelligenceJobAsync(
                context.MailboxId, jobId, upload.Sha256, upload.Content, cancellationToken).ConfigureAwait(false);

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
                DateTime.UtcNow), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(upload.Content);
        }
    }

    private async Task<List<MailIntelligenceUploadEnvelopeDto>> PrepareAsync(
        MailAccount account,
        IReadOnlyList<IntelligenceMessageCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var prepared = new List<MailIntelligenceUploadEnvelopeDto>(candidates.Count);
        var gate = new SemaphoreSlim(DocumentPreparationConcurrency, DocumentPreparationConcurrency);
        var results = new MailIntelligenceUploadEnvelopeDto?[candidates.Count];

        await Task.WhenAll(candidates.Select(async (candidate, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                results[index] = await PrepareOneAsync(account, candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A message whose body cannot be read is skipped rather than failing the job.
                results[index] = null;
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        gate.Dispose();
        prepared.AddRange(results.Where(static item => item is not null)!);
        return prepared;
    }

    private async Task<MailIntelligenceUploadEnvelopeDto> PrepareOneAsync(
        MailAccount account,
        IntelligenceMessageCandidate candidate,
        CancellationToken cancellationToken)
    {
        var content = await messageResolver.GetContentAsync(account.Id, candidate, cancellationToken).ConfigureAwait(false);
        var from = content.From.Count > 0
            ? content.From
            : [new MailAddress(candidate.Sender, candidate.SenderName)];

        // The content profile pins the canonical projection and the hash. It has nothing
        // to do with vectors any more.
        var processed = new MailContentProcessor(new HtmlContentSanitizer())
            .Prepare(from, candidate.Subject, content.Body, MailContentProfile.Default);

        return new MailIntelligenceUploadEnvelopeDto
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
    }

    // ---- polling and import --------------------------------------------------------

    public async Task PollAsync(CancellationToken cancellationToken = default)
    {
        if (!_acceptingWork)
        {
            return;
        }

        var jobs = await store.GetUnfinishedJobsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await PollJobAsync(job, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                SetSnapshot(job.LocalAccountId, snapshot => snapshot with { ErrorCode = exception.Message });
            }
        }
    }

    private async Task PollJobAsync(MailIntelligenceJobState job, CancellationToken cancellationToken)
    {
        var remote = await apiClient
            .GetMailIntelligenceJobAsync(job.MailboxId, job.JobId, cancellationToken)
            .ConfigureAwait(false);

        if (remote is null)
        {
            // The server deleted it, which only happens after both stages were acknowledged.
            await store.DeleteJobAsync(job.JobId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var updated = job with
        {
            Status = remote.Status,
            Classification = job.Classification with { Status = remote.Classification.Status, PageCount = remote.Classification.PageCount, Digest = remote.Classification.ResultDigest },
            Enrichment = job.Enrichment with { Status = remote.Summarization.Status, PageCount = remote.Summarization.PageCount, Digest = remote.Summarization.ResultDigest },
            FailedCount = remote.Classification.FailedCount + remote.Summarization.FailedCount,
        };
        await store.UpsertJobAsync(updated, cancellationToken).ConfigureAwait(false);

        SetSnapshot(job.LocalAccountId, snapshot => snapshot with
        {
            Status = MailIntelligenceJobStatus.Downloading,
            Classification = new MailIntelligenceStageProgress(remote.Classification.Status, remote.Classification.PageCount, updated.Classification.IsImported, remote.Classification.IsAcknowledged),
            Enrichment = new MailIntelligenceStageProgress(remote.Summarization.Status, remote.Summarization.PageCount, updated.Enrichment.IsImported, remote.Summarization.IsAcknowledged),
            FailedMessageCount = updated.FailedCount,
        });

        // Classification is published first and imported first, so labels and priority appear before
        // any headline exists.
        if (remote.Classification.Status == MailIntelligenceStageStatuses.Published && !updated.Classification.IsAcknowledged)
        {
            await ImportClassificationStageAsync(updated, remote, cancellationToken).ConfigureAwait(false);
        }

        if (remote.Summarization.Status == MailIntelligenceStageStatuses.Published && !updated.Enrichment.IsAcknowledged)
        {
            await ImportEnrichmentStageAsync(updated, remote, cancellationToken).ConfigureAwait(false);
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
        for (var page = 0; page < remote.Classification.PageCount; page++)
        {
            var dto = await apiClient
                .GetClassificationResultPageAsync(job.MailboxId, job.JobId, page, cancellationToken)
                .ConfigureAwait(false);

            var artifacts = dto.Items
                .Select(item => new ClassificationArtifact(
                    new MailArtifactKey(item.Identity.RemoteMessageId, item.Identity.ContentHash),
                    [.. item.Labels.Select(static label => label.ToString().ToLowerInvariant())],
                    item.Priority.ToString().ToLowerInvariant(),
                    item.Action.ToString().ToLowerInvariant(),
                    item.IncludeInBriefing,
                    item.CompletedUtc.UtcDateTime,
                    MapSignals(item.Signals)))
                .ToArray();

            var desired = await ResolveDesiredHashesAsync(
                job.LocalAccountId, artifacts.Select(static x => x.Key.RemoteMessageId), cancellationToken).ConfigureAwait(false);

            var result = await store.ImportClassificationPageAsync(
                job.LocalAccountId, artifacts, MapFailures(dto.Failures), desired, cancellationToken).ConfigureAwait(false);
            imported += result.Imported;
        }

        // The import has committed, so the stage can be acknowledged.
        await store.MarkStageImportedAsync(job.JobId, MailIntelligenceStageKind.Classification, cancellationToken).ConfigureAwait(false);
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

    private async Task ImportEnrichmentStageAsync(
        MailIntelligenceJobState job, MailIntelligenceJobDto remote, CancellationToken cancellationToken)
    {
        for (var page = 0; page < remote.Summarization.PageCount; page++)
        {
            var dto = await apiClient
                .GetEnrichmentResultPageAsync(job.MailboxId, job.JobId, page, cancellationToken)
                .ConfigureAwait(false);

            var artifacts = dto.Items
                .Select(item => new EnrichmentArtifact(
                    new MailArtifactKey(item.Identity.RemoteMessageId, item.Identity.ContentHash),
                    item.Headline,
                    item.Summary,
                    item.CompletedUtc.UtcDateTime))
                .ToArray();

            var desired = await ResolveDesiredHashesAsync(
                job.LocalAccountId, artifacts.Select(static x => x.Key.RemoteMessageId), cancellationToken).ConfigureAwait(false);

            await store.ImportEnrichmentPageAsync(
                job.LocalAccountId, artifacts, MapFailures(dto.Failures), desired, cancellationToken).ConfigureAwait(false);
        }

        await store.MarkStageImportedAsync(job.JobId, MailIntelligenceStageKind.Enrichment, cancellationToken).ConfigureAwait(false);
        await apiClient.AcknowledgeMailIntelligenceStageAsync(
            job.MailboxId, job.JobId, "summarization", remote.Summarization.ResultDigest ?? string.Empty, cancellationToken).ConfigureAwait(false);
        await store.MarkStageAcknowledgedAsync(job.JobId, MailIntelligenceStageKind.Enrichment, cancellationToken).ConfigureAwait(false);

        messenger.Send(new IntelligenceMetadataChanged(
            job.LocalAccountId, new HashSet<string>(StringComparer.Ordinal), IntelligenceMetadataChangeScope.Messages));
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

        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var remoteMessageId in remoteMessageIds.Distinct(StringComparer.Ordinal))
        {
            var candidate = await messageResolver
                .FindCandidateAsync(localAccountId, remoteMessageId, cancellationToken)
                .ConfigureAwait(false);
            if (candidate is null)
            {
                continue;
            }

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
        }

        return hashes;
    }

    private static IReadOnlyList<MailIntelligenceItemFailure> MapFailures(IReadOnlyList<MailIntelligenceFailureDto> failures)
        => [.. failures.Select(failure => new MailIntelligenceItemFailure(
            new MailArtifactKey(failure.Identity.RemoteMessageId, failure.Identity.ContentHash),
            string.Equals(failure.Stage, "summarization", StringComparison.Ordinal)
                ? MailIntelligenceStageKind.Enrichment
                : MailIntelligenceStageKind.Classification,
            failure.ErrorCode))];

    /// <summary>
    /// Keeps the raw probabilities the server sent, in the lowercase names the rest of the
    /// app uses. They are stored rather than dropped so a threshold change can be applied
    /// to mail that has already been classified.
    /// </summary>
    private static ClassificationSignals MapSignals(MailClassificationSignalsDto signals)
        => new(
            signals.LabelProbabilities.ToDictionary(
                static entry => entry.Key.ToString().ToLowerInvariant(),
                static entry => entry.Value,
                StringComparer.Ordinal),
            signals.BriefingProbability,
            signals.PriorityScore,
            signals.PriorityProbabilities.ToDictionary(
                static entry => entry.Key.ToString().ToLowerInvariant(),
                static entry => entry.Value,
                StringComparer.Ordinal),
            signals.TopAction.ToString().ToLowerInvariant(),
            signals.TopActionProbability,
            signals.ActionConfidence);

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
        var envelope = uploadBuilder.BuildSingle(context.WinoUserId, context.MailboxId, projection);

        try
        {
            var language = translationService.CurrentLanguageModel?.Code ?? "en-US";
            var response = await apiClient
                .AnalyzeMailAsync(context.MailboxId, envelope, language, cancellationToken)
                .ConfigureAwait(false);

            var desired = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [projection.RemoteMessageId] = projection.ContentHash,
            };

            // Both artifacts are imported before the caller's UI action completes.
            await store.ImportClassificationPageAsync(
                localMailAccountId,
                [new ClassificationArtifact(
                    new MailArtifactKey(response.Classification.Identity.RemoteMessageId, response.Classification.Identity.ContentHash),
                    [.. response.Classification.Labels.Select(static label => label.ToString().ToLowerInvariant())],
                    response.Classification.Priority.ToString().ToLowerInvariant(),
                    response.Classification.Action.ToString().ToLowerInvariant(),
                    response.Classification.IncludeInBriefing,
                    response.Classification.CompletedUtc.UtcDateTime,
                    MapSignals(response.Classification.Signals))],
                [],
                desired,
                cancellationToken).ConfigureAwait(false);

            if (response.Summary is { } summary)
            {
                await store.ImportEnrichmentPageAsync(
                    localMailAccountId,
                    [new EnrichmentArtifact(
                        new MailArtifactKey(summary.Identity.RemoteMessageId, summary.Identity.ContentHash),
                        summary.Headline,
                        summary.Summary,
                        summary.CompletedUtc.UtcDateTime)],
                    [],
                    desired,
                    cancellationToken).ConfigureAwait(false);
            }

            messenger.Send(new IntelligenceMetadataChanged(
                localMailAccountId, new HashSet<string>(StringComparer.Ordinal) { projection.RemoteMessageId }, IntelligenceMetadataChangeScope.Messages));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
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
            await PollJobAsync(job, cancellationToken).ConfigureAwait(false);
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

    public async Task ResetLocalStateAsync(CancellationToken cancellationToken = default)
    {
        _acceptingWork = false;
        await jobRegistry.CancelAllAndWaitAsync().ConfigureAwait(false);
        await store.DeleteDatabaseAsync(cancellationToken).ConfigureAwait(false);
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

    private void ThrowIfWorkUnavailable()
    {
        if (!_acceptingWork)
        {
            throw new InvalidOperationException("Mail intelligence is not accepting work.");
        }

        if (entitlementService is not null && !entitlementService.Current.CanConsumeQuota)
        {
            throw new InvalidOperationException(
                $"Mail intelligence is unavailable: {entitlementService.Current.State}.");
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

        if (_resumeLoop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifecycle.Dispose();
    }
}
