#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Services;

public sealed class IntelligenceSearchService(
    IAccountService accountService,
    IWinoAccountApiClient apiClient,
    IIntelligenceMessageContextResolver messageResolver,
    IMailService mailService,
    ILocalIntelligenceStore localStore,
    ILocalIntelligenceSearchEngine localSearch) : IIntelligenceSearchService
{
    public async Task<IntelligenceMailSearchResult> SearchAsync(IntelligenceSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Query) || options.Folders.Count == 0)
            return new([], []);

        var serverMailboxes = await apiClient.GetSemanticMailboxesAsync(cancellationToken).ConfigureAwait(false);
        var scopes = new List<LocalIntelligenceSearchScope>();
        var states = new Dictionary<Guid, LocalIntelligenceMailboxState>();
        var omissions = new List<IntelligenceSearchOmission>();
        foreach (var group in options.Folders.GroupBy(static folder => folder.MailAccountId))
        {
            var account = await accountService.GetAccountAsync(group.Key).ConfigureAwait(false);
            if (account is null || !account.Preferences.IsSemanticIndexingEnabled)
                continue;
            var mailbox = serverMailboxes.SingleOrDefault(item => item.ProviderType == (int)account.ProviderType &&
                string.Equals(item.Address.Trim(), account.Address.Trim(), StringComparison.OrdinalIgnoreCase));
            if (mailbox is null)
                continue;
            var state = await localStore.GetMailboxStateAsync(account.Id, cancellationToken).ConfigureAwait(false);
            if (state is null || state.MailboxId != mailbox.MailboxId)
            {
                omissions.Add(new(mailbox.MailboxId, IntelligenceMailboxCompatibilityStatuses.NotInitialized, "localIndexUnavailable"));
                continue;
            }
            states[account.Id] = state;
            scopes.Add(new(
                account.Id,
                mailbox.MailboxId,
                group.Select(static folder => folder.RemoteFolderId)
                    .Where(static id => !string.IsNullOrWhiteSpace(id))
                    .ToHashSet(StringComparer.Ordinal)));
        }

        if (scopes.Count == 0)
            return new([], omissions);
        var request = new IntelligenceSearchPlanRequest(
            options.Query.Trim(),
            string.IsNullOrWhiteSpace(CultureInfo.CurrentUICulture.Name) ? "en-US" : CultureInfo.CurrentUICulture.Name,
            TimeZoneInfo.Local.Id,
            Math.Clamp(options.Limit, 1, 100),
            scopes.Select(scope => new IntelligenceSearchTargetRequest(
                scope.MailboxId,
                states[scope.LocalAccountId].IntelligenceVersion,
                states[scope.LocalAccountId].IndexEpoch)).ToArray());
        var response = await apiClient.CreateIntelligenceSearchPlanAsync(request, cancellationToken).ConfigureAwait(false);
        omissions.AddRange(response.Mailboxes
            .Where(static mailbox => mailbox.Status is not IntelligenceMailboxCompatibilityStatuses.Ready and
                                                   not IntelligenceMailboxCompatibilityStatuses.UpgradeAvailable)
            .Select(static mailbox => new IntelligenceSearchOmission(mailbox.MailboxId, mailbox.Status, mailbox.ErrorCode)));

        var executableMailboxIds = response.Plans.SelectMany(static plan => plan.MailboxIds).ToHashSet();
        var matches = (await localSearch.SearchAsync(
            response,
            scopes.Where(scope => executableMailboxIds.Contains(scope.MailboxId)).ToArray(),
            // Apply the explicit UI filters below before enforcing the caller's limit. Asking
            // the engine for its bounded maximum prevents an unread/flagged filter from
            // discarding a full page merely because the first semantic hits do not qualify.
            100,
            cancellationToken).ConfigureAwait(false))
            .Where(match => options.IsUnread != true || !match.Document.IsRead)
            .Where(match => options.IsFlagged != true || match.Document.IsFlagged)
            .Where(match => options.HasAttachments != true || match.Document.HasAttachments)
            .Take(options.Limit)
            .ToArray();
        var candidates = new Dictionary<(Guid AccountId, string RemoteId), IntelligenceMessageCandidate>();
        foreach (var accountId in matches.Select(static match => match.LocalAccountId).Distinct())
        {
            foreach (var candidate in await messageResolver.GetCandidatesAsync(accountId, null, cancellationToken).ConfigureAwait(false))
                candidates.TryAdd((accountId, candidate.RemoteMessageId), candidate);
        }
        var selected = matches
            .Select(match => candidates.GetValueOrDefault((match.LocalAccountId, match.RemoteMessageId)))
            .Where(static candidate => candidate is not null)
            .Cast<IntelligenceMessageCandidate>()
            .ToArray();
        var hydrated = await mailService.GetMailItemsAsync(selected.Select(static candidate => candidate.ProviderMessageId)).ConfigureAwait(false);
        var mailByIdentity = hydrated
            .Where(static mail => mail.AssignedAccount is not null)
            .GroupBy(static mail => (mail.AssignedAccount.Id, mail.Id))
            .ToDictionary(static group => group.Key, static group => group.First());
        var result = new List<MailCopy>(matches.Length);
        foreach (var match in matches)
        {
            var candidate = candidates.GetValueOrDefault((match.LocalAccountId, match.RemoteMessageId));
            if (candidate is not null && mailByIdentity.TryGetValue((match.LocalAccountId, candidate.ProviderMessageId), out var mail))
                result.Add(mail);
        }
        return new(result, omissions);
    }
}
