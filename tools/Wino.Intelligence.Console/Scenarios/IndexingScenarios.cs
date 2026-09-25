using System.Diagnostics;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Intelligence.ConsoleApp.Hosting;

namespace Wino.Intelligence.ConsoleApp.Scenarios;

/// <summary>Submitting mail for processing and managing the resulting jobs and local results.</summary>
internal static class IndexingScenarios
{
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(30);

    public static async Task IndexInboxAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var inbox = await IntelligenceFlow.GetInboxAsync(context).ConfigureAwait(false);
        if (inbox is null)
        {
            ConsoleOutput.Error("This account has no inbox folder. Synchronize it first.");
            return;
        }

        var count = ConsoleOutput.AskNumber("How many of the newest inbox messages", MailAccountPreferencesDefaults.LatestCount, 1, 100_000);
        await IndexFolderAsync(context, inbox, count, cancellationToken).ConfigureAwait(false);
    }

    public static async Task IndexCustomFolderAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var folders = (await context.Get<IFolderService>().GetFoldersAsync(context.Account.Id).ConfigureAwait(false))
            .Where(IntelligenceFolderFilter.IsSelectable)
            .OrderBy(static folder => folder.FolderName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (folders.Length == 0)
        {
            ConsoleOutput.Error("No folder of this account can be indexed. Synchronize it first.");
            return;
        }

        var index = ConsoleOutput.Choose("Folder", folders, static folder => $"{folder.FolderName} ({folder.SpecialFolderType})");
        if (index < 0)
            return;

        ConsoleOutput.Muted("  More than 1000 messages exercises the coordinator's per-job split.");
        var count = ConsoleOutput.AskNumber("How many of the newest messages", MailAccountPreferencesDefaults.LatestCount, 1, 100_000);
        await IndexFolderAsync(context, folders[index], count, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Delete everything local for the account, then index the inbox again. One timing for both.</summary>
    public static async Task ReindexInboxAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var inbox = await IntelligenceFlow.GetInboxAsync(context).ConfigureAwait(false);
        if (inbox is null)
        {
            ConsoleOutput.Error("This account has no inbox folder. Synchronize it first.");
            return;
        }

        var count = ConsoleOutput.AskNumber("How many of the newest inbox messages", MailAccountPreferencesDefaults.LatestCount, 1, 100_000);
        if (!ConsoleOutput.Confirm($"Delete this account's local intelligence and index {count} inbox message(s) again?"))
            return;

        var clock = Stopwatch.StartNew();
        await IntelligenceFlow.DeleteIndexAsync(context, cancellationToken).ConfigureAwait(false);
        await IndexFolderAsync(context, inbox, count, cancellationToken).ConfigureAwait(false);
        ConsoleOutput.Header($"Re-index finished in {ConsoleOutput.Elapsed(clock.Elapsed)}.");
    }

    /// <summary>
    /// The management page end to end: consent, add-on, enable, coverage, plan, start, follow.
    /// </summary>
    public static async Task IndexFolderAsync(ScenarioContext context, MailItemFolder folder, int count, CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var accountId = context.Account.Id;

        ConsoleOutput.Header($"Indexing the newest {count} message(s) of {folder.FolderName} for {context.Account.Address}");

        if (!await IntelligenceFlow.EnsureConsentAsync(context, cancellationToken).ConfigureAwait(false))
            return;
        if (!await IntelligenceFlow.EnsureEntitlementAsync(context, cancellationToken).ConfigureAwait(false))
            return;

        await IntelligenceFlow.SaveFolderCoverageAsync(context, folder.RemoteFolderId, count).ConfigureAwait(false);
        await IntelligenceFlow.SetIndexingEnabledAsync(context, true).ConfigureAwait(false);

        var plan = await IntelligenceFlow.PlanAsync(context, folder.RemoteFolderId, count, cancellationToken).ConfigureAwait(false);
        ConsoleOutput.KeyValue("Indexable messages in account", plan.AccountMessageCount);
        ConsoleOutput.KeyValue($"Indexable in {folder.FolderName}", plan.FolderMessageCount);
        ConsoleOutput.KeyValue("Selected", plan.SelectedIds.Length);
        ConsoleOutput.KeyValue("Already processed", plan.AlreadyProcessedCount);
        ConsoleOutput.KeyValue("To submit", plan.PendingCount);

        if (plan.SelectedIds.Length == 0)
        {
            ConsoleOutput.Warning("Nothing to index. Synchronize the account first.");
            return;
        }

        var coordinator = context.Get<IMailIntelligenceCoordinator>();
        using var monitor = new JobMonitor(accountId);
        monitor.Mark($"plan ready ({ConsoleOutput.Elapsed(total.Elapsed)} before submission)");

        try
        {
            await coordinator.StartProcessingAsync(accountId, plan.SelectedIds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ConsoleOutput.Error($"Indexing could not start: {IntelligenceFlow.DescribeError(exception)}");
            return;
        }

        ConsoleOutput.Muted("  Following the job. The coordinator long-polls each unfinished job and collects results as soon as they are published. Ctrl+C stops watching.");
        var final = await WaitAsync(context, monitor, cancellationToken).ConfigureAwait(false);

        monitor.PrintMilestones();
        if (final is not null)
            ConsoleOutput.Header($"{final.Status} after {ConsoleOutput.Elapsed(total.Elapsed)}{(final.ErrorCode is { Length: > 0 } error ? $": {IntelligenceFlow.DescribeError(new Exception(error))}" : string.Empty)}");

        await ArtifactReport.PrintSummaryAsync(context, plan.SelectedIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The single-message route the reader header's process button uses (messages:analyze). It
    /// returns both artifacts synchronously, so it is the quickest way to see what the models do.
    /// </summary>
    public static async Task AnalyzeOneAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var inbox = await IntelligenceFlow.GetInboxAsync(context).ConfigureAwait(false);
        if (inbox is null)
        {
            ConsoleOutput.Error("This account has no inbox folder.");
            return;
        }

        var candidates = await ConsoleOutput.TimedAsync("Candidate lookup", () => context.Get<IIntelligenceMessageContextResolver>()
            .GetCandidatesAsync(context.Account.Id, null, cancellationToken)).ConfigureAwait(false);
        var recent = candidates
            .Where(candidate => candidate.RemoteFolderIds.Contains(inbox.RemoteFolderId, StringComparer.Ordinal))
            .OrderByDescending(static candidate => candidate.ReceivedAt)
            .Take(20)
            .ToArray();
        if (recent.Length == 0)
        {
            ConsoleOutput.Warning("The inbox has no indexable messages.");
            return;
        }

        var processed = await context.Get<IMailIntelligenceStore>().GetProcessedMessageIdsAsync(
            context.Account.Id, recent.Select(static candidate => candidate.RemoteMessageId).ToArray(), cancellationToken).ConfigureAwait(false);

        var index = ConsoleOutput.Choose("Message", recent, candidate =>
            $"{candidate.ReceivedAt.ToLocalTime():g}  {(processed.Contains(candidate.RemoteMessageId) ? "[done] " : string.Empty)}{Trim(candidate.SenderName, 20),-20}  {Trim(candidate.Subject, 60)}");
        if (index < 0)
            return;

        if (!await IntelligenceFlow.EnsureConsentAsync(context, cancellationToken).ConfigureAwait(false) ||
            !await IntelligenceFlow.EnsureEntitlementAsync(context, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var candidate = recent[index];
        try
        {
            await ConsoleOutput.TimedAsync("messages:analyze round trip (prepare, encrypt, analyze, import)", () => context.Get<IMailIntelligenceCoordinator>()
                .ProcessMessageAsync(context.Account.Id, candidate.RemoteMessageId, cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ConsoleOutput.Error($"Analyze failed: {IntelligenceFlow.DescribeError(exception)}");
            return;
        }

        await ArtifactReport.PrintMessagesAsync(context, [candidate], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One poll of every unfinished job, as the resume loop does after a restart. Use it after
    /// quitting mid-job to check that collection resumes from the stored stage state.
    /// </summary>
    public static async Task PollAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var coordinator = context.Get<IMailIntelligenceCoordinator>();
        var jobs = await coordinator.GetJobsAsync(context.Account.Id, cancellationToken).ConfigureAwait(false);
        PrintLocalJobs(jobs);
        if (jobs.Count == 0)
            return;

        using var monitor = new JobMonitor(context.Account.Id);
        await ConsoleOutput.TimedAsync("Poll", () => coordinator.PollAsync(cancellationToken)).ConfigureAwait(false);

        if (ConsoleOutput.Confirm("Keep following until the jobs finish?"))
        {
            await coordinator.ResumeAsync(cancellationToken).ConfigureAwait(false);
            await WaitAsync(context, monitor, cancellationToken).ConfigureAwait(false);
            monitor.PrintMilestones();
        }

        PrintLocalJobs(await coordinator.GetJobsAsync(context.Account.Id, cancellationToken).ConfigureAwait(false));
    }

    public static async Task ControlJobsAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var coordinator = context.Get<IMailIntelligenceCoordinator>();
        var jobs = await coordinator.GetJobsAsync(context.Account.Id, cancellationToken).ConfigureAwait(false);
        PrintLocalJobs(jobs);
        if (jobs.Count == 0)
            return;

        string[] actions = ["Cancel every job of this account", "Cancel one job", "Re-poll one job"];
        var action = ConsoleOutput.Choose("Action", actions, static text => text);
        if (action < 0)
            return;

        if (action == 0)
        {
            await ConsoleOutput.TimedAsync("Cancel", () => coordinator.CancelAsync(context.Account.Id, cancellationToken)).ConfigureAwait(false);
        }
        else
        {
            var jobIndex = ConsoleOutput.Choose("Job", jobs, static job => $"{job.JobId:D} {job.Status} {job.MessageCount} message(s)");
            if (jobIndex < 0)
                return;

            var jobId = jobs[jobIndex].JobId;
            if (action == 1)
                await ConsoleOutput.TimedAsync("Cancel job", () => coordinator.CancelJobAsync(jobId, cancellationToken)).ConfigureAwait(false);
            else
                await ConsoleOutput.TimedAsync("Re-poll job", () => coordinator.RetryJobAsync(jobId, cancellationToken)).ConfigureAwait(false);
        }

        PrintLocalJobs(await coordinator.GetJobsAsync(context.Account.Id, cancellationToken).ConfigureAwait(false));
    }

    public static async Task DeleteIndexAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        if (!ConsoleOutput.Confirm($"Cancel jobs, delete local intelligence and turn indexing off for {context.Account.Address}?"))
            return;

        await IntelligenceFlow.DeleteIndexAsync(context, cancellationToken).ConfigureAwait(false);
        ConsoleOutput.Success("Local intelligence deleted.");
    }

    internal static void PrintLocalJobs(IReadOnlyList<MailIntelligenceJobState> jobs)
    {
        ConsoleOutput.Header($"Jobs tracked on this device: {jobs.Count}");
        foreach (var job in jobs)
        {
            ConsoleOutput.Info($"  {job.JobId:D} {job.Status,-10} {job.MessageCount,5} msg  created {job.CreatedUtc.ToLocalTime():g}  key {job.ResultKeyId ?? "-"}");
            ConsoleOutput.Info($"      classification {job.Classification.Status} p{job.Classification.PageCount}{(job.Classification.IsImported ? " imported" : string.Empty)}{(job.Classification.IsAcknowledged ? " acked" : string.Empty)}" +
                               $" | enrichment {job.Enrichment.Status} p{job.Enrichment.PageCount}{(job.Enrichment.IsImported ? " imported" : string.Empty)}{(job.Enrichment.IsAcknowledged ? " acked" : string.Empty)}" +
                               (job.LastError is { Length: > 0 } error ? $" | last error {error}" : string.Empty));
        }
    }

    /// <summary>
    /// Waits for a terminal snapshot. Ctrl+C stops watching and offers to cancel the jobs; the
    /// jobs keep running otherwise, as they do when the app's page is closed.
    /// </summary>
    private static async Task<MailIntelligenceJobSnapshot?> WaitAsync(ScenarioContext context, JobMonitor monitor, CancellationToken cancellationToken)
    {
        try
        {
            var final = await monitor.WaitAsync(JobTimeout, cancellationToken).ConfigureAwait(false);
            if (final is null)
                ConsoleOutput.Warning($"No terminal state after {ConsoleOutput.Elapsed(JobTimeout)}. The jobs are still tracked; use the poll scenario later.");
            return final;
        }
        catch (OperationCanceledException)
        {
            ConsoleOutput.Warning("Stopped watching.");
            if (ConsoleOutput.Confirm("Cancel this account's jobs too?"))
                await context.Get<IMailIntelligenceCoordinator>().CancelAsync(context.Account.Id).ConfigureAwait(false);
            return monitor.Last;
        }
    }

    internal static string Trim(string? text, int length)
        => string.IsNullOrEmpty(text) ? string.Empty : text.Length <= length ? text : text[..(length - 1)] + "…";
}

internal static class MailAccountPreferencesDefaults
{
    public static int LatestCount => Wino.Core.Domain.Entities.Shared.MailAccountPreferences.DefaultLatestMessageCount;
}
