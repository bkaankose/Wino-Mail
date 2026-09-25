using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Intelligence.Keys;
using Wino.Core.Domain.Models.Accounts;
using Wino.Intelligence.ConsoleApp.Hosting;
using Wino.Services;

namespace Wino.Intelligence.ConsoleApp.Scenarios;

/// <summary>Read-only views of the client and server state, plus mailbox registration.</summary>
internal static class InspectionScenarios
{
    /// <summary>
    /// Everything that decides whether indexing can run, from both sides. Server jobs are
    /// compared with the jobs this device tracks, so orphans and other devices' jobs stand out.
    /// </summary>
    public static async Task StatusAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var apiClient = context.Get<IWinoAccountApiClient>();
        var store = context.Get<IMailIntelligenceStore>();
        var coordinator = context.Get<IMailIntelligenceCoordinator>();
        var winoAccount = await WinoAccountStatus.PrintAsync(context, cancellationToken).ConfigureAwait(false);
        if (winoAccount is null)
            return;

        var account = await context.ReloadAccountAsync().ConfigureAwait(false);
        ConsoleOutput.Header($"Mail account {account.Address}");
        ConsoleOutput.KeyValue("Provider", account.ProviderType);
        ConsoleOutput.KeyValue("Intelligence enabled", account.Preferences.IsSemanticIndexingEnabled);
        ConsoleOutput.KeyValue("Index new mail automatically", account.Preferences.AutomaticallyIndexNewMessages);
        ConsoleOutput.KeyValue("Daily briefing", account.Preferences.IsDailyBriefingEnabled);
        ConsoleOutput.KeyValue("Coverage configured", account.Preferences.IsIntelligenceCoverageInitialized);
        ConsoleOutput.KeyValue("Coverage rules", string.Join("; ", account.Preferences.IntelligenceFolderCoverageRules
            .Select(static rule => $"{IndexingScenarios.Trim(rule.RemoteFolderId, 16)} {rule.Mode} {rule.LatestMessageCount}")) is { Length: > 0 } rules ? rules : "-");

        var access = await store.GetAccessAsync(account.Id, cancellationToken).ConfigureAwait(false);
        ConsoleOutput.KeyValue("Server mailbox id (local)", access is { } row ? $"{row.MailboxId:D} pack={row.HasAiPack} consent={row.HasConsent}" : "not resolved yet");

        var activeKey = await context.Get<IIntelligenceResultKeyStore>().GetActiveKeyAsync(winoAccount.Id, cancellationToken).ConfigureAwait(false);
        ConsoleOutput.KeyValue("Device result key", activeKey is null ? "none" : $"{activeKey.KeyId} (created {activeKey.CreatedUtc.ToLocalTime():g})");

        var state = await ConsoleOutput.TimedAsync("Account intelligence state",
            () => coordinator.GetStateAsync(account.Id, cancellationToken)).ConfigureAwait(false);
        ConsoleOutput.KeyValue("Processed messages", state.ProcessedMessageCount);
        ConsoleOutput.KeyValue("Unprocessed indexable messages", state.WaitingMessageCount);
        ConsoleOutput.KeyValue("Unfinished local jobs", state.ActiveJobCount);

        var localJobs = await coordinator.GetJobsAsync(account.Id, cancellationToken).ConfigureAwait(false);
        IndexingScenarios.PrintLocalJobs(localJobs);

        try
        {
            var serverJobs = await ConsoleOutput.TimedAsync("GET jobs",
                () => apiClient.GetMailIntelligenceJobsAsync(cancellationToken: cancellationToken)).ConfigureAwait(false);
            ConsoleOutput.Header($"Unfinished jobs on the server for this Wino user: {serverJobs.Jobs.Count}");
            foreach (var job in serverJobs.Jobs)
            {
                var tracked = await store.GetJobAsync(job.JobId, cancellationToken).ConfigureAwait(false) is not null;
                var owner = tracked ? "tracked here"
                    : activeKey is not null && job.ResultKeyId == activeKey.KeyId ? "this device's key, NOT tracked (orphan)"
                    : job.ResultKeyId is null ? "legacy (no result key)"
                    : "another device or an old key";
                var mailbox = access is { } known && known.MailboxId == job.MailboxId ? "this mailbox" : $"mailbox {job.MailboxId:D}";

                ConsoleOutput.Info($"  {job.JobId:D} {job.Status,-10} {job.MessageCount,5} msg  {mailbox}  {owner}");
                ConsoleOutput.Info($"      classification {job.Classification.Status} {job.Classification.CompletedCount} done {job.Classification.FailedCount} failed p{job.Classification.PageCount}{(job.Classification.IsAcknowledged ? " acked" : string.Empty)}" +
                                   $" | enrichment {job.Enrichment.Status} {job.Enrichment.CompletedCount} done {job.Enrichment.FailedCount} failed p{job.Enrichment.PageCount}{(job.Enrichment.IsAcknowledged ? " acked" : string.Empty)}" +
                                   $" | created {job.CreatedUtc.ToLocalTime():g}{(job.ExpiresUtc is { } expires ? $" expires {expires.ToLocalTime():g}" : string.Empty)}" +
                                   (job.FailureCode is { Length: > 0 } failure ? $" | failure {failure}" : string.Empty));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ConsoleOutput.Error($"Listing server jobs failed: {IntelligenceFlow.DescribeError(exception)}");
        }
    }

    /// <summary>The newest processed messages and the briefing the app would show.</summary>
    public static async Task ArtifactsAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var candidates = await ConsoleOutput.TimedAsync("Candidate lookup", () => context.Get<IIntelligenceMessageContextResolver>()
            .GetCandidatesAsync(context.Account.Id, null, cancellationToken)).ConfigureAwait(false);
        var ids = candidates.Select(static candidate => candidate.RemoteMessageId).ToArray();

        await ArtifactReport.PrintSummaryAsync(context, ids, cancellationToken).ConfigureAwait(false);

        var processed = await context.Get<IMailIntelligenceStore>().GetProcessedMessageIdsAsync(context.Account.Id, ids, cancellationToken).ConfigureAwait(false);
        var count = ConsoleOutput.AskNumber("Show how many of the newest processed messages", 10, 0, 200);
        var newest = candidates
            .Where(candidate => processed.Contains(candidate.RemoteMessageId))
            .OrderByDescending(static candidate => candidate.ReceivedAt)
            .Take(count)
            .ToArray();
        await ArtifactReport.PrintMessagesAsync(context, newest, cancellationToken).ConfigureAwait(false);

        var briefing = await ConsoleOutput.TimedAsync("Briefing facts", () => context.Get<ILocalIntelligenceService>()
            .GetBriefingFactsAsync(TimeZoneInfo.Local, cancellationToken: cancellationToken)).ConfigureAwait(false);
        ConsoleOutput.Header($"Daily briefing (all accounts): {briefing.TotalCount} card(s), {briefing.IgnoredCount} ignored");
        foreach (var day in briefing.Days.Take(7))
        {
            ConsoleOutput.Info($"  {day.LocalDate:ddd d MMM}");
            foreach (var fact in day.Facts.Where(fact => fact.LocalAccountId == context.Account.Id).Take(10))
            {
                ConsoleOutput.Info($"    [{fact.Priority}] {IndexingScenarios.Trim(fact.Headline is { Length: > 0 } headline ? headline : fact.Subject, 70)}");
                if (!string.IsNullOrWhiteSpace(fact.Summary))
                    ConsoleOutput.Muted($"        {IndexingScenarios.Trim(fact.Summary, 110)}");
            }
        }
    }

    /// <summary>
    /// Pushes the local mailbox list to the server (the account data export, without preferences)
    /// so every mail account has a server mailbox id. Intelligence cannot submit without one.
    /// </summary>
    public static async Task RegisterMailboxesAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var apiClient = context.Get<IWinoAccountApiClient>();
        var before = await apiClient.GetMailboxesAsync(cancellationToken).ConfigureAwait(false);
        PrintMailboxes("Server mailboxes before", before.Mailboxes.Select(static mailbox => (mailbox.Address, mailbox.MailboxId, mailbox.ProviderType)));

        if (!ConsoleOutput.Confirm("Replace the server mailbox list with this device's accounts? Preferences are not uploaded."))
            return;

        var result = await ConsoleOutput.TimedAsync("Export mailboxes", () => context.Get<IWinoAccountDataSyncService>()
            .ExportAsync(new WinoAccountSyncSelection(IncludePreferences: false, IncludeAccounts: true), cancellationToken)).ConfigureAwait(false);
        ConsoleOutput.Success($"Exported {result.ExportedMailboxCount} mailbox(es).");

        var after = await apiClient.GetMailboxesAsync(cancellationToken).ConfigureAwait(false);
        PrintMailboxes("Server mailboxes after", after.Mailboxes.Select(static mailbox => (mailbox.Address, mailbox.MailboxId, mailbox.ProviderType)));

        var changed = after.Mailboxes.Where(mailbox => before.Mailboxes.All(previous => previous.MailboxId != mailbox.MailboxId)).ToArray();
        if (changed.Length > 0)
            ConsoleOutput.Warning($"  {changed.Length} mailbox id(s) are new. Jobs submitted under an old id can no longer be reached.");
    }

    private static void PrintMailboxes(string title, IEnumerable<(string Address, Guid? MailboxId, int ProviderType)> mailboxes)
    {
        ConsoleOutput.Header(title);
        foreach (var (address, mailboxId, providerType) in mailboxes)
            ConsoleOutput.Info($"  {mailboxId?.ToString("D") ?? "(no id)",-36}  {address} ({(Wino.Core.Domain.Enums.MailProviderType)providerType})");
    }
}

/// <summary>The Wino account, add-on and quota, as the settings page shows them.</summary>
internal static class WinoAccountStatus
{
    public static async Task<WinoAccount?> PrintAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var winoAccount = await context.Get<IDatabaseService>().Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false);
        ConsoleOutput.Header("Wino account");
        if (winoAccount is null)
        {
            ConsoleOutput.Error("  Not signed in. Sign in to a Wino account in the app first.");
            return null;
        }

        ConsoleOutput.KeyValue("Email", winoAccount.Email);
        ConsoleOutput.KeyValue("Access token expires", winoAccount.AccessTokenExpiresAtUtc.ToLocalTime().ToString("g"));
        ConsoleOutput.KeyValue("Entitlement", context.Get<IWinoIntelligenceEntitlementService>().Current.State);

        var apiClient = context.Get<IWinoAccountApiClient>();
        var billing = await apiClient.GetBillingStatusAsync(cancellationToken).ConfigureAwait(false);
        ConsoleOutput.KeyValue("AI Pack", billing.IsSuccess && billing.Result?.AiPack is { } pack
            ? $"{pack.Status} access={pack.HasAccess} period ends {pack.CurrentPeriodEndUtc?.ToLocalTime().ToString("g") ?? "-"}"
            : $"unavailable ({billing.ErrorCode})");

        var usage = await apiClient.GetAiUsageAsync(cancellationToken).ConfigureAwait(false);
        ConsoleOutput.KeyValue("AI usage", usage.IsSuccess && usage.Result is { } quota
            ? $"{string.Join(", ", quota.Buckets.Select(bucket => $"{bucket.Bucket} {bucket.Used}/{bucket.Limit}"))}; resets {quota.ResetsAtUtc?.ToLocalTime().ToString("g") ?? "-"}"
            : $"unavailable ({usage.ErrorCode})");

        try
        {
            var consent = await apiClient.GetIntelligenceConsentAsync(cancellationToken).ConfigureAwait(false);
            IntelligenceFlow.PrintConsent(consent);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ConsoleOutput.KeyValue("Consent", $"unavailable ({IntelligenceFlow.DescribeError(exception)})");
        }

        return winoAccount;
    }
}
