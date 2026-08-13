#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Services;

public sealed class IntelligenceSearchService(
    IAccountService accountService,
    IWinoAccountApiClient apiClient,
    IIntelligenceMessageContextResolver messageResolver,
    IMailService mailService) : IIntelligenceSearchService
{
    public async Task<IntelligenceMailSearchResult> SearchAsync(IntelligenceSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Query) || options.Folders.Count == 0)
            return new([], []);

        var serverMailboxes = await apiClient.GetSemanticMailboxesAsync(cancellationToken).ConfigureAwait(false);
        var scopes = new List<IntelligenceMailboxSearchScopeDto>();
        var localAccountByMailbox = new Dictionary<Guid, Guid>();
        foreach (var group in options.Folders.GroupBy(x => x.MailAccountId))
        {
            var account = await accountService.GetAccountAsync(group.Key).ConfigureAwait(false);
            if (account is null || !account.Preferences.IsSemanticIndexingEnabled)
                continue;
            var mailbox = serverMailboxes.SingleOrDefault(x => x.ProviderType == (int)account.ProviderType &&
                string.Equals(x.Address.Trim(), account.Address.Trim(), StringComparison.OrdinalIgnoreCase));
            if (mailbox is null)
                continue;

            scopes.Add(new IntelligenceMailboxSearchScopeDto(mailbox.MailboxId, new IntelligenceSearchFilterDto(
                ProviderFolderIds: group.Select(x => x.RemoteFolderId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray(),
                IsUnread: options.IsUnread,
                IsFlagged: options.IsFlagged,
                HasAttachments: options.HasAttachments)));
            localAccountByMailbox[mailbox.MailboxId] = account.Id;
        }

        if (scopes.Count == 0)
            return new([], []);

        var language = string.IsNullOrWhiteSpace(CultureInfo.CurrentUICulture.Name)
            ? "en-US"
            : CultureInfo.CurrentUICulture.Name;
        var request = new IntelligenceSemanticSearchRequest(
            options.Query.Trim(),
            scopes,
            Math.Clamp(options.Limit, 1, 100),
            null,
            TimeZoneInfo.Local.Id,
            language,
            true);
        var response = await apiClient.SearchIntelligenceAsync(request, cancellationToken).ConfigureAwait(false);

        var mappingByMailbox = new Dictionary<Guid, IReadOnlyDictionary<string, Guid>>();
        foreach (var mailboxGroup in response.Items.GroupBy(x => x.MailboxId))
        {
            if (!localAccountByMailbox.TryGetValue(mailboxGroup.Key, out var accountId))
                continue;
            var wanted = mailboxGroup.Select(x => x.RemoteMessageId).ToHashSet(StringComparer.Ordinal);
            mappingByMailbox[mailboxGroup.Key] = (await messageResolver.GetCandidatesAsync(
                    accountId, null, cancellationToken).ConfigureAwait(false))
                .Where(candidate => wanted.Contains(candidate.RemoteMessageId))
                .GroupBy(candidate => candidate.RemoteMessageId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().UniqueId, StringComparer.Ordinal);
        }

        var hydrated = new List<MailCopy>(response.Items.Count);
        foreach (var item in response.Items)
        {
            if (!mappingByMailbox.TryGetValue(item.MailboxId, out var mapping) ||
                !mapping.TryGetValue(item.RemoteMessageId, out var localMessageId))
                continue;
            var mail = await mailService.GetSingleMailItemAsync(localMessageId).ConfigureAwait(false);
            if (mail is not null)
                hydrated.Add(mail);
        }

        return new(
            hydrated,
            response.Mailboxes.Where(x => x.OmissionReason is not null)
                .Select(x => new IntelligenceSearchOmission(x.MailboxId, x.State, x.OmissionReason)).ToArray());
    }
}
