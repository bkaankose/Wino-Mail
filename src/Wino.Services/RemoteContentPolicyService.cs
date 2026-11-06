using System;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Services;

public class RemoteContentPolicyService : IRemoteContentPolicyService
{
    private readonly IRemoteContentRuleStore _ruleStore;
    private readonly IRemoteContentSessionService _sessionService;
    private readonly IPreferencesService _preferencesService;
    private readonly IContactService _contactService;
    private readonly IRecipientHistoryService _recipientHistoryService;
    private readonly ILogger _logger = Log.ForContext<RemoteContentPolicyService>();

    public RemoteContentPolicyService(IRemoteContentRuleStore ruleStore,
                                      IRemoteContentSessionService sessionService,
                                      IPreferencesService preferencesService,
                                      IContactService contactService,
                                      IRecipientHistoryService recipientHistoryService)
    {
        _ruleStore = ruleStore;
        _sessionService = sessionService;
        _preferencesService = preferencesService;
        _contactService = contactService;
        _recipientHistoryService = recipientHistoryService;
    }

    public async Task<RemoteContentDecision> ResolveAsync(MailCopy? mail, MimeMessage message, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        var senderAddress = message.From?.Mailboxes.FirstOrDefault()?.Address?.Trim() ?? string.Empty;
        var senderDomain = RemoteContentRulePattern.GetDomain(senderAddress);

        RemoteContentDecision Decide(RemoteContentMode mode, RemoteContentDecisionReason reason, RemoteContentSenderRule? rule = null)
            => new(mode, reason, rule, senderAddress, senderDomain);

        Guid? sessionId = mail != null
            ? mail.UniqueId
            : string.IsNullOrWhiteSpace(filePath) ? null : _sessionService.GetFileId(filePath);

        RemoteContentMode sessionMode = default;
        var hasSessionOverride = sessionId.HasValue && _sessionService.TryGet(sessionId.Value, out sessionMode);

        if (mail?.AssignedFolder?.SpecialFolderType == SpecialFolderType.Junk)
        {
            return hasSessionOverride
                ? Decide(sessionMode, RemoteContentDecisionReason.SessionOverride)
                : Decide(RemoteContentMode.BlockAll, RemoteContentDecisionReason.Junk);
        }

        if (hasSessionOverride)
            return Decide(sessionMode, RemoteContentDecisionReason.SessionOverride);

        if (!string.IsNullOrEmpty(senderAddress))
        {
            var rule = await _ruleStore.FindAsync(senderAddress).ConfigureAwait(false);

            if (rule != null)
            {
                await TouchRuleAsync(rule).ConfigureAwait(false);

                var reason = rule.Kind == RemoteContentRuleKind.Address
                    ? RemoteContentDecisionReason.AddressRule
                    : RemoteContentDecisionReason.DomainRule;

                return Decide(MapPolicy(rule.Policy), reason, rule);
            }

            if (_preferencesService.LoadRemoteContentFromContacts)
            {
                var accountId = GetAccountId(mail);
                var contact = await _contactService.GetContactByAddressAsync(accountId, senderAddress).ConfigureAwait(false);

                // Auto-collected cards come from mail traffic, not from the user, so they do not count as contacts.
                if (contact is { IsAutoCollected: false })
                    return Decide(RemoteContentMode.LoadEverything, RemoteContentDecisionReason.Contact);
            }

            if (_preferencesService.LoadRemoteContentFromRecipients && GetAccountId(mail) is Guid recipientAccountId)
            {
                var history = await _recipientHistoryService.FindAsync(recipientAccountId, senderAddress).ConfigureAwait(false);

                if (history is { SentCount: > 0 })
                    return Decide(RemoteContentMode.LoadEverything, RemoteContentDecisionReason.Recipient);
            }
        }

        return Decide(_preferencesService.RemoteContentMode, RemoteContentDecisionReason.Mode);
    }

    public static RemoteContentMode MapPolicy(RemoteContentSenderPolicy policy) => policy switch
    {
        RemoteContentSenderPolicy.LoadEverything => RemoteContentMode.LoadEverything,
        RemoteContentSenderPolicy.ImagesOnly => RemoteContentMode.BlockTrackers,
        _ => RemoteContentMode.BlockAll
    };

    private static Guid? GetAccountId(MailCopy? mail)
        => mail?.AssignedAccount?.Id ?? mail?.AssignedFolder?.MailAccountId;

    private async Task TouchRuleAsync(RemoteContentSenderRule rule)
    {
        try
        {
            var now = DateTime.UtcNow;
            await _ruleStore.TouchAsync(rule.Id, now).ConfigureAwait(false);
            rule.LastAppliedUtc = now;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not record when a remote content rule was applied.");
        }
    }
}
