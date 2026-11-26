using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Exchange.WebServices.Data;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Task = System.Threading.Tasks.Task;

namespace Wino.Providers.Exchange.Synchronizers.Exchange;

// Inbox rules over EWS (MS-OXWSRULES GetInboxRules 3.1.4.1, UpdateInboxRules 3.1.4.2). The MAPI synchronizer overrides
// every member with MS-OXORULE. A rule Wino cannot represent is listed read-only and never sent back: UpdateInboxRules
// replaces a whole rule, so writing one would drop what the filter model cannot hold.
public partial class ExchangeSynchronizer : IProviderMailFilterSynchronizer
{
    public virtual async Task<IReadOnlyList<MailFilter>> GetProviderFiltersAsync(CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);
        var rules = await service.GetInboxRules(cancellationToken).ConfigureAwait(false);
        var senders = await ResolveDirectorySendersAsync(service, rules, cancellationToken).ConfigureAwait(false);
        var deletedItems = await GetDeletedItemsRemoteIdAsync().ConfigureAwait(false);

        return rules.Select(rule => FromEwsRule(rule, senders).ToMailFilter(deletedItems)).ToList();
    }

    public virtual async Task<MailFilter> CreateProviderFilterAsync(MailFilter filter, CancellationToken cancellationToken = default)
    {
        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);
        var before = await service.GetInboxRules(cancellationToken).ConfigureAwait(false);

        // New rules go after the existing ones; Priority starts at 1 (MS-OXWSRULES 2.2.4.12).
        var rule = ExchangeServerRule.FromMailFilter(filter, remoteId: null, sequence: before.Count + 1);

        // RemoveOutlookRuleBlob stays false: removing the blob would delete the user's Outlook client-only rules.
        await service.UpdateInboxRules([new CreateRuleOperation(ToEwsRule(rule, new Dictionary<string, EmailAddress>()))], false, cancellationToken).ConfigureAwait(false);

        // UpdateInboxRulesResponse carries no rule id; the created rule is the one id that was not there before.
        var knownIds = before.Select(existing => existing.Id).ToHashSet(StringComparer.Ordinal);
        var after = await service.GetInboxRules(cancellationToken).ConfigureAwait(false);
        var created = after.Where(candidate => !knownIds.Contains(candidate.Id)).ToList();
        if (created.Count != 1)
            throw new InvalidOperationException($"Exchange created the rule, but {created.Count} new rules were found, so it cannot be identified. Refresh the rules list.");

        var senders = await ResolveDirectorySendersAsync(service, created, cancellationToken).ConfigureAwait(false);
        var deletedItems = await GetDeletedItemsRemoteIdAsync().ConfigureAwait(false);
        return ExchangeServerRule.WithLocalMetadata(FromEwsRule(created[0], senders).ToMailFilter(deletedItems), filter);
    }

    public virtual async Task<MailFilter> UpdateProviderFilterAsync(MailFilter filter, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filter.RemoteId))
            throw new InvalidOperationException("The Exchange rule has no remote identifier.");

        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);
        var (current, currentSenders) = await RequireEditableEwsRuleAsync(service, filter.RemoteId, cancellationToken).ConfigureAwait(false);

        var rule = ExchangeServerRule.FromMailFilter(filter, filter.RemoteId, current.Priority);
        var ewsRule = ToEwsRule(rule, StoredDirectorySenders(current, currentSenders));
        ewsRule.Id = filter.RemoteId;
        await service.UpdateInboxRules([new SetRuleOperation(ewsRule)], false, cancellationToken).ConfigureAwait(false);

        var updated = (await service.GetInboxRules(cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Id == filter.RemoteId)
            ?? throw new InvalidOperationException("Exchange no longer lists the updated rule.");

        var senders = await ResolveDirectorySendersAsync(service, [updated], cancellationToken).ConfigureAwait(false);
        var deletedItems = await GetDeletedItemsRemoteIdAsync().ConfigureAwait(false);
        return ExchangeServerRule.WithLocalMetadata(FromEwsRule(updated, senders).ToMailFilter(deletedItems), filter);
    }

    public virtual async Task DeleteProviderFilterAsync(string remoteId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(remoteId))
            throw new ArgumentException("A remote Exchange rule identifier is required.", nameof(remoteId));

        var service = await CreateServiceAsync(cancellationToken).ConfigureAwait(false);
        await RequireEditableEwsRuleAsync(service, remoteId, cancellationToken).ConfigureAwait(false);
        await service.UpdateInboxRules([new DeleteRuleOperation(remoteId)], false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The server rule by id, refused when it holds anything Wino cannot represent.</summary>
    private static async Task<(Rule Rule, Dictionary<string, string> DirectorySenders)> RequireEditableEwsRuleAsync(ExchangeService service, string remoteId, CancellationToken cancellationToken)
    {
        var current = (await service.GetInboxRules(cancellationToken).ConfigureAwait(false)).FirstOrDefault(candidate => candidate.Id == remoteId)
            ?? throw new InvalidOperationException("The Exchange rule no longer exists on the server. Refresh the rules list.");

        var senders = await ResolveDirectorySendersAsync(service, [current], cancellationToken).ConfigureAwait(false);
        if (!FromEwsRule(current, senders).IsEditable)
            throw new InvalidOperationException("The Exchange rule has conditions or actions Wino cannot represent, so Wino leaves it unchanged.");

        return (current, senders);
    }

    protected async Task<string?> GetDeletedItemsRemoteIdAsync()
        => (await ExchangeChangeProcessor.GetLocalFoldersAsync(Account.Id).ConfigureAwait(false))
            .FirstOrDefault(folder => folder.SpecialFolderType == SpecialFolderType.Deleted)?.RemoteFolderId;

    /// <summary>
    /// The primary SMTP address of every directory sender (RoutingType EX, the legacy DN) the rules' From predicates
    /// name, by ResolveNames over the directory only (MS-OXWSRSLNM 3.1.4.1). Rules made with Exchange's own tools
    /// (New-InboxRule, OWA) store a mailbox sender as its directory entry, which GetInboxRules returns as EX. A DN that
    /// does not resolve to exactly one SMTP mailbox (deleted mailbox, foreign X500) is left out, which keeps its rule
    /// read-only.
    /// </summary>
    private static async Task<Dictionary<string, string>> ResolveDirectorySendersAsync(ExchangeService service, IEnumerable<Rule> rules, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var distinguishedNames = rules
            .SelectMany(rule => rule.Conditions.FromAddresses)
            .Where(address => IsDirectoryAddress(address) && !string.IsNullOrWhiteSpace(address.Address))
            .Select(address => address.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var distinguishedName in distinguishedNames)
        {
            NameResolutionCollection resolutions;
            try
            {
                resolutions = await service.ResolveName(distinguishedName, ResolveNameSearchLocation.DirectoryOnly, false, cancellationToken).ConfigureAwait(false);
            }
            catch (ServiceResponseException ex) when (ex.ErrorCode == ServiceError.ErrorNameResolutionNoResults)
            {
                continue;
            }

            if (resolutions.Count == 1
                && resolutions[0].Mailbox is { Address: { Length: > 0 } smtp } mailbox
                && SmtpRoutingType.Equals(mailbox.RoutingType, StringComparison.OrdinalIgnoreCase))
            {
                resolved[distinguishedName] = smtp;
            }
        }

        return resolved;
    }

    private const string SmtpRoutingType = "SMTP";
    private const string DirectoryRoutingType = "EX";

    private static bool IsDirectoryAddress(EmailAddress address)
        => DirectoryRoutingType.Equals(address.RoutingType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The rule's stored directory senders by the SMTP address Wino shows for them. An update sends each one back as
    /// the EX address the server holds while the filter still names it; an address the filter adds goes as SMTP.
    /// </summary>
    private static Dictionary<string, EmailAddress> StoredDirectorySenders(Rule rule, IReadOnlyDictionary<string, string> directorySenders)
    {
        var stored = new Dictionary<string, EmailAddress>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in rule.Conditions.FromAddresses)
        {
            if (IsDirectoryAddress(address) && directorySenders.TryGetValue(address.Address, out var smtp))
                stored.TryAdd(smtp, new EmailAddress(address.Name, address.Address, address.RoutingType));
        }

        return stored;
    }

    private static ExchangeServerRule FromEwsRule(Rule rule, IReadOnlyDictionary<string, string> directorySenders)
    {
        var conditions = rule.Conditions;
        var actions = rule.Actions;

        var result = new ExchangeServerRule
        {
            RemoteId = rule.Id,
            Name = rule.DisplayName ?? string.Empty,
            IsEnabled = rule.IsEnabled,
            Sequence = rule.Priority,
            StopProcessing = actions.StopProcessingRules,
            HasError = rule.IsInError,
            HasAttachments = conditions.HasAttachments,
            Importance = conditions.Importance?.ToString(),
            MarkAsRead = actions.MarkAsRead,
            MoveToFolderRemoteId = actions.MoveToFolder?.UniqueId,
            SoftDelete = actions.Delete,
            PermanentDelete = actions.PermanentDelete,
        };

        result.SubjectContains.AddRange(conditions.ContainsSubjectStrings);
        result.BodyContains.AddRange(conditions.ContainsBodyStrings);
        result.SenderContains.AddRange(conditions.ContainsSenderStrings);

        var fromAddressesAreSmtp = true;
        foreach (var address in conditions.FromAddresses)
        {
            if (string.IsNullOrWhiteSpace(address.Address))
                fromAddressesAreSmtp = false;
            else if (IsDirectoryAddress(address))
            {
                if (directorySenders.TryGetValue(address.Address, out var smtp))
                    result.FromAddresses.Add(smtp);
                else
                    fromAddressesAreSmtp = false;
            }
            else if (address.RoutingType != null && !address.RoutingType.Equals(SmtpRoutingType, StringComparison.OrdinalIgnoreCase))
                fromAddressesAreSmtp = false;
            else
                result.FromAddresses.Add(address.Address);
        }

        // IsNotSupported (MS-OXWSRULES 2.2.4.12): the rule holds something EWS itself cannot express.
        result.IsRepresentable = !rule.IsNotSupported
            && fromAddressesAreSmtp
            && !HasUnrepresentablePredicates(conditions)
            && !HasAnyPredicate(rule.Exceptions)
            && !HasUnrepresentableActions(actions);

        return result;
    }

    private static bool HasUnrepresentablePredicates(RulePredicates p)
        => p.Categories.Count > 0
        || p.ContainsHeaderStrings.Count > 0
        || p.ContainsRecipientStrings.Count > 0
        || p.ContainsSubjectOrBodyStrings.Count > 0
        || p.FlaggedForAction != null
        || p.FromConnectedAccounts.Count > 0
        || p.IsApprovalRequest
        || p.IsAutomaticForward
        || p.IsAutomaticReply
        || p.IsEncrypted
        || p.IsMeetingRequest
        || p.IsMeetingResponse
        || p.IsNonDeliveryReport
        || p.IsPermissionControlled
        || p.IsSigned
        || p.IsVoicemail
        || p.IsReadReceipt
        || p.ItemClasses.Count > 0
        || p.MessageClassifications.Count > 0
        || p.NotSentToMe
        || p.SentCcMe
        || p.SentOnlyToMe
        || p.SentToAddresses.Count > 0
        || p.SentToMe
        || p.SentToOrCcMe
        || p.Sensitivity != null
        || p.WithinDateRange.Start != null
        || p.WithinDateRange.End != null
        || p.WithinSizeRange.MinimumSize != null
        || p.WithinSizeRange.MaximumSize != null;

    private static bool HasAnyPredicate(RulePredicates p)
        => HasUnrepresentablePredicates(p)
        || p.ContainsSubjectStrings.Count > 0
        || p.ContainsBodyStrings.Count > 0
        || p.ContainsSenderStrings.Count > 0
        || p.FromAddresses.Count > 0
        || p.HasAttachments
        || p.Importance != null;

    private static bool HasUnrepresentableActions(RuleActions a)
        => a.AssignCategories.Count > 0
        || a.CopyToFolder != null
        || a.ForwardAsAttachmentToRecipients.Count > 0
        || a.ForwardToRecipients.Count > 0
        || a.MarkImportance != null
        || a.RedirectToRecipients.Count > 0
        || a.SendSMSAlertToRecipients.Count > 0
        || a.ServerReplyWithMessage != null;

    private static Rule ToEwsRule(ExchangeServerRule rule, IReadOnlyDictionary<string, EmailAddress> storedDirectorySenders)
    {
        var ewsRule = new Rule
        {
            DisplayName = rule.Name,
            Priority = Math.Max(1, rule.Sequence),
            IsEnabled = rule.IsEnabled
        };

        var conditions = ewsRule.Conditions;
        foreach (var address in rule.FromAddresses)
        {
            if (storedDirectorySenders.TryGetValue(address, out var stored))
                conditions.FromAddresses.Add(stored);
            else
                conditions.FromAddresses.Add(address);
        }
        conditions.ContainsSubjectStrings.AddRange(rule.SubjectContains);
        conditions.ContainsBodyStrings.AddRange(rule.BodyContains);
        conditions.ContainsSenderStrings.AddRange(rule.SenderContains);
        conditions.HasAttachments = rule.HasAttachments;
        if (rule.Importance != null)
            conditions.Importance = Enum.Parse<Importance>(rule.Importance);

        var actions = ewsRule.Actions;
        actions.MarkAsRead = rule.MarkAsRead;
        if (rule.MoveToFolderRemoteId != null)
            actions.MoveToFolder = new FolderId(rule.MoveToFolderRemoteId);
        actions.Delete = rule.SoftDelete;
        actions.PermanentDelete = rule.PermanentDelete;
        actions.StopProcessingRules = rule.StopProcessing;

        return ewsRule;
    }
}
