using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Intelligence.ConsoleApp.Hosting;
using Wino.Mail.Contracts.Intelligence;
using Wino.Messaging.UI;

namespace Wino.Intelligence.ConsoleApp.Scenarios;

/// <summary>
/// The decisions the intelligence pages make, expressed against the same services. Each method
/// names the ViewModel member it mirrors, so a change there can be carried over here.
/// </summary>
internal static class IntelligenceFlow
{
    /// <summary>WinoIntelligenceCoordinator.IsCurrentConsent: active and on the current policy.</summary>
    public static bool IsCurrent(IntelligenceConsentDto consent)
        => consent.Status == ConsentStatuses.Active &&
           string.Equals(consent.AcceptedPolicyVersion, consent.CurrentPolicyVersion, StringComparison.Ordinal);

    public static void PrintConsent(IntelligenceConsentDto consent)
    {
        ConsoleOutput.KeyValue("Consent status", consent.Status);
        ConsoleOutput.KeyValue("Current policy version", consent.CurrentPolicyVersion);
        ConsoleOutput.KeyValue("Accepted policy version", consent.AcceptedPolicyVersion ?? "-");
        ConsoleOutput.KeyValue("Accepted at", consent.AcceptedAtUtc?.ToLocalTime().ToString("g") ?? "-");
        ConsoleOutput.KeyValue("Revoked at", consent.RevokedAtUtc?.ToLocalTime().ToString("g") ?? "-");
        ConsoleOutput.KeyValue("Server data deletion", consent.DataDeletionStatus);
    }

    /// <summary>
    /// WinoAccountManagementPageViewModel.SetIntelligenceConsentAsync(true): read the current
    /// policy version, then accept exactly that version from the consent page source. The app
    /// shows the policy in a dialog; the console prints its address and asks.
    /// </summary>
    public static async Task<bool> EnsureConsentAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var apiClient = context.Get<IWinoAccountApiClient>();
        var consent = await ConsoleOutput.TimedAsync("GET consent",
            () => apiClient.GetIntelligenceConsentAsync(cancellationToken)).ConfigureAwait(false);

        if (IsCurrent(consent))
        {
            ConsoleOutput.Success($"Consent is active for policy {consent.CurrentPolicyVersion}.");
            return true;
        }

        ConsoleOutput.Warning("Wino Intelligence consent is not active for the current policy.");
        PrintConsent(consent);
        ConsoleOutput.Info($"  Privacy policy: {consent.PrivacyPolicyUrl}");

        if (!ConsoleOutput.Confirm($"Accept the Wino Intelligence policy {consent.CurrentPolicyVersion}?"))
        {
            ConsoleOutput.Warning("Consent was not accepted. Intelligence stays off.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(consent.CurrentPolicyVersion))
            throw new InvalidOperationException(Wino.Mail.Api.Contracts.Common.ApiErrorCodes.ValidationFailed);

        var accepted = await ConsoleOutput.TimedAsync("PUT consent", () => apiClient.AcceptIntelligenceConsentAsync(
            consent.CurrentPolicyVersion, ConsentActionSources.ConsentPage, cancellationToken)).ConfigureAwait(false);

        await RefreshAccessAsync(context, cancellationToken).ConfigureAwait(false);

        if (!IsCurrent(accepted))
        {
            ConsoleOutput.Error("The server did not activate consent.");
            PrintConsent(accepted);
            return false;
        }

        ConsoleOutput.Success("Consent accepted.");
        return true;
    }

    /// <summary>
    /// SetIntelligenceConsentAsync(false) followed by DisableAndClearAllLocalIntelligenceAsync:
    /// revoke on the server, then delete local results and turn intelligence off for every account.
    /// </summary>
    public static async Task<IntelligenceConsentDto> RevokeConsentAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var apiClient = context.Get<IWinoAccountApiClient>();
        var coordinator = context.Get<IMailIntelligenceCoordinator>();
        var accountService = context.Get<IAccountService>();

        var revoked = await ConsoleOutput.TimedAsync("DELETE consent",
            () => apiClient.RevokeIntelligenceConsentAsync(ConsentActionSources.ConsentPage, cancellationToken)).ConfigureAwait(false);

        foreach (var account in await accountService.GetAccountsAsync().ConfigureAwait(false) ?? [])
        {
            await coordinator.DeleteLocalIntelligenceAsync(account.Id, cancellationToken).ConfigureAwait(false);
            ConsoleOutput.Muted($"  Local intelligence deleted for {account.Address}.");

            if (!account.Preferences.IsSemanticIndexingEnabled)
                continue;

            account.Preferences.IsSemanticIndexingEnabled = false;
            await accountService.UpdateAccountAsync(account).ConfigureAwait(false);
            ConsoleOutput.Muted($"  Intelligence turned off for {account.Address}.");
        }

        await RefreshAccessAsync(context, cancellationToken).ConfigureAwait(false);
        await context.ReloadAccountAsync().ConfigureAwait(false);
        return revoked;
    }

    /// <summary>The add-on must be active before anything is submitted (MailIntelligenceCoordinator.ThrowIfWorkUnavailable).</summary>
    public static async Task<bool> EnsureEntitlementAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var entitlement = await ConsoleOutput.TimedAsync("Entitlement refresh",
            () => context.Get<IWinoIntelligenceEntitlementService>().RefreshAsync(cancellationToken)).ConfigureAwait(false);

        if (entitlement.CanConsumeQuota)
            return true;

        ConsoleOutput.Error($"Wino Intelligence is not usable: entitlement is {entitlement.State}.");
        if (entitlement.State is WinoIntelligenceEntitlementState.NoSubscription or WinoIntelligenceEntitlementState.Expired)
            ConsoleOutput.Info("  The Wino account needs an active AI Pack on this API.");
        else if (entitlement.State == WinoIntelligenceEntitlementState.QuotaExhausted)
            ConsoleOutput.Info("  The monthly AI quota is used up.");
        return false;
    }

    /// <summary>
    /// WinoIntelligenceManagementPageViewModel.SetSemanticIndexingEnabledAsync. The VM only assigns
    /// the preference when enabling (see the if without braces there); this sets it both ways.
    /// </summary>
    public static async Task SetIndexingEnabledAsync(ScenarioContext context, bool isEnabled)
    {
        var account = await context.ReloadAccountAsync().ConfigureAwait(false);
        if (account.Preferences.IsSemanticIndexingEnabled == isEnabled)
            return;

        account.Preferences.IsSemanticIndexingEnabled = isEnabled;
        await context.Get<IAccountService>().UpdateAccountAsync(account).ConfigureAwait(false);
        WeakReferenceMessenger.Default.Send(new WinoIntelligenceAccessChanged());
        ConsoleOutput.Success($"Intelligence {(isEnabled ? "enabled" : "disabled")} for {account.Address}.");
    }

    /// <summary>
    /// WinoIntelligenceManagementPageViewModel.SetIntelligenceFolderSelectionAsync: include the folder
    /// and store its latest-N rule, so the app's coverage editor shows what the console indexed.
    /// Other included folders and their rules are kept.
    /// </summary>
    public static async Task SaveFolderCoverageAsync(ScenarioContext context, string remoteFolderId, int latestCount)
    {
        var account = await context.ReloadAccountAsync().ConfigureAwait(false);
        var preferences = account.Preferences;

        var selection = preferences.IsIntelligenceFolderSelectionInitialized
            ? preferences.SelectedIntelligenceFolderIds.ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        selection.Add(remoteFolderId);

        var rules = preferences.IntelligenceFolderCoverageRules
            .Where(rule => !string.Equals(rule.RemoteFolderId, remoteFolderId, StringComparison.Ordinal))
            .Append(SemanticIndexFolderCoverageRule.Latest(remoteFolderId, latestCount))
            .ToList();

        preferences.IsIntelligenceFolderSelectionInitialized = true;
        preferences.SelectedIntelligenceFolderIds = selection;
        preferences.IsIntelligenceCoverageInitialized = true;
        preferences.IntelligenceFolderCoverageRules = rules;
        if (string.IsNullOrWhiteSpace(preferences.IntelligenceDefaultCoverageStorage))
            preferences.IntelligenceDefaultCoverageRule = SemanticIndexFolderCoverageRule.Latest(string.Empty, MailAccountPreferences.DefaultLatestMessageCount);
        preferences.PrepareForStorage();

        await context.Get<IAccountService>().UpdateAccountPreferencesAsync(preferences).ConfigureAwait(false);
    }

    /// <summary>
    /// WinoIntelligenceManagementPageViewModel.DeleteSemanticIndexAsync without its dialog:
    /// cancel jobs, delete local results, turn indexing off.
    /// </summary>
    public static async Task DeleteIndexAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        var coordinator = context.Get<IMailIntelligenceCoordinator>();
        var accountId = context.Account.Id;

        await ConsoleOutput.TimedAsync("Cancel jobs", () => coordinator.CancelAsync(accountId, cancellationToken)).ConfigureAwait(false);
        await ConsoleOutput.TimedAsync("Delete local intelligence", () => coordinator.DeleteLocalIntelligenceAsync(accountId, cancellationToken)).ConfigureAwait(false);
        await SetIndexingEnabledAsync(context, false).ConfigureAwait(false);
    }

    public static async Task<MailItemFolder?> GetInboxAsync(ScenarioContext context)
        => await context.Get<IFolderService>().GetSpecialFolderByAccountIdAsync(context.Account.Id, SpecialFolderType.Inbox).ConfigureAwait(false);

    /// <summary>
    /// The management page's plan: one coverage inventory for the account, resolved against a
    /// latest-N rule for the folder (WinoIntelligenceManagementPageViewModel.RecomputeCoverage).
    /// </summary>
    public static async Task<CoveragePlan> PlanAsync(ScenarioContext context, string remoteFolderId, int latestCount, CancellationToken cancellationToken)
    {
        var accountId = context.Account.Id;
        var inventory = await ConsoleOutput.TimedAsync("Coverage inventory",
            () => context.Get<IIntelligenceMessageContextResolver>().GetCoverageInventoryAsync(accountId, cancellationToken)).ConfigureAwait(false);

        var selection = IntelligenceCoverageCalculator.Resolve(
            inventory, [SemanticIndexFolderCoverageRule.Latest(remoteFolderId, latestCount)], DateTimeOffset.UtcNow);
        var selectedIds = selection.ToRemoteMessageIds();

        var processed = await context.Get<IMailIntelligenceStore>()
            .GetProcessedMessageIdsAsync(accountId, selectedIds, cancellationToken).ConfigureAwait(false);

        return new CoveragePlan(
            inventory.TotalMessageCount,
            inventory.GetFolderIndices(remoteFolderId).Length,
            selectedIds,
            processed.Count);
    }

    /// <summary>Marks access as changed and refreshes what the intelligence gates read.</summary>
    private static async Task RefreshAccessAsync(ScenarioContext context, CancellationToken cancellationToken)
    {
        try
        {
            await context.Get<IWinoAccountIntelligenceSnapshotService>().RefreshAsync(cancellationToken).ConfigureAwait(false);
            await context.Get<IWinoIntelligenceEntitlementService>().RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ConsoleOutput.Muted($"  Refreshing the intelligence snapshot failed: {exception.Message}");
        }

        WeakReferenceMessenger.Default.Send(new WinoIntelligenceAccessChanged());
    }

    public static string DescribeError(Exception exception)
    {
        var message = exception.Message;
        var translated = WinoAccountApiErrorTranslator.Translate(message);
        return string.Equals(translated, message, StringComparison.Ordinal) ? message : $"{translated} ({message})";
    }
}

internal sealed record CoveragePlan(int AccountMessageCount, int FolderMessageCount, string[] SelectedIds, int AlreadyProcessedCount)
{
    public int PendingCount => SelectedIds.Length - AlreadyProcessedCount;
}
