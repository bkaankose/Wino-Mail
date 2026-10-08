using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Requests.Mail;
using Wino.Messaging.Server;

namespace Wino.Core.Services;

public class DraftSyncRetryService : IDraftSyncRetryService
{
    public const int MaximumAttemptCount = 5;

    private static readonly TimeSpan[] RetryBackoff =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2)
    ];

    private readonly IMailService _mailService;
    private readonly IMimeFileService _mimeFileService;
    private readonly ISynchronizerFactory _synchronizerFactory;

    public DraftSyncRetryService(
        IMailService mailService,
        IMimeFileService mimeFileService,
        ISynchronizerFactory synchronizerFactory)
    {
        _mailService = mailService;
        _mimeFileService = mimeFileService;
        _synchronizerFactory = synchronizerFactory;
    }

    public async Task<bool> QueueEligibleRetriesAsync(Guid accountId, IWinoSynchronizerBase synchronizer)
    {
        if (synchronizer == null)
            return false;

        var queuedAny = false;
        var drafts = await _mailService.GetUnsyncedLocalDraftsAsync(accountId).ConfigureAwait(false);

        foreach (var draft in drafts)
        {
            if (synchronizer.HasPendingOperation(draft.UniqueId) ||
                !IsEligibleForRetry(draft, synchronizer.Account.ProviderType, DateTime.UtcNow))
            {
                continue;
            }

            var request = await CreateRetryRequestAsync(draft).ConfigureAwait(false);
            await _mailService.MarkDraftSyncAttemptAsync(draft.UniqueId).ConfigureAwait(false);
            synchronizer.QueueRequest(new CreateDraftRequest(request));
            queuedAny = true;
        }

        return queuedAny;
    }

    public async Task RetryNowAsync(MailCopy draftCopy)
    {
        if (draftCopy?.AssignedAccount == null || !draftCopy.IsLocalDraft)
            return;

        var synchronizer = await _synchronizerFactory
            .GetAccountSynchronizerAsync(draftCopy.AssignedAccount.Id)
            .ConfigureAwait(false);

        if (synchronizer == null || synchronizer.HasPendingOperation(draftCopy.UniqueId))
            return;

        var request = await CreateRetryRequestAsync(draftCopy).ConfigureAwait(false);
        await _mailService.MarkDraftSyncAttemptAsync(draftCopy.UniqueId).ConfigureAwait(false);
        synchronizer.QueueRequest(new CreateDraftRequest(request));

        WeakReferenceMessenger.Default.Send(new NewMailSynchronizationRequested(new MailSynchronizationOptions
        {
            AccountId = draftCopy.AssignedAccount.Id,
            Type = MailSynchronizationType.ExecuteRequests
        }));
    }

    public static bool IsEligibleForRetry(MailCopy draft, MailProviderType providerType, DateTime utcNow)
    {
        if (draft == null || !draft.IsLocalDraft || draft.DraftSyncAttemptCount >= MaximumAttemptCount)
            return false;

        if (providerType == MailProviderType.IMAP4 && draft.DraftSyncState != DraftSyncState.SyncFailed)
            return false;

        if (!draft.LastDraftSyncAttemptUtc.HasValue)
            return true;

        var backoffIndex = Math.Clamp(draft.DraftSyncAttemptCount, 0, RetryBackoff.Length - 1);
        return utcNow - draft.LastDraftSyncAttemptUtc.Value >= RetryBackoff[backoffIndex];
    }

    private async Task<DraftPreparationRequest> CreateRetryRequestAsync(MailCopy draftCopy)
    {
        var mimeInformation = await _mimeFileService
            .GetMimeMessageInformationAsync(draftCopy.FileId, draftCopy.AssignedAccount.Id)
            .ConfigureAwait(false);

        var (reason, referenceMailCopy) = await ResolveDraftContextAsync(draftCopy, mimeInformation.MimeMessage).ConfigureAwait(false);

        return new DraftPreparationRequest(
            draftCopy.AssignedAccount,
            draftCopy,
            mimeInformation.MimeMessage.GetBase64MimeMessage(),
            reason,
            referenceMailCopy);
    }

    private async Task<(DraftCreationReason Reason, MailCopy ReferenceMailCopy)> ResolveDraftContextAsync(
        MailCopy draftCopy,
        MimeMessage mimeMessage)
    {
        var inReplyTo = mimeMessage?.InReplyTo;
        if (string.IsNullOrWhiteSpace(inReplyTo) && mimeMessage?.Headers.Contains(HeaderId.InReplyTo) == true)
            inReplyTo = mimeMessage.Headers[HeaderId.InReplyTo];

        inReplyTo = MailHeaderExtensions.StripAngleBrackets(inReplyTo);
        if (string.IsNullOrWhiteSpace(inReplyTo))
            return (DraftCreationReason.Empty, null);

        var referenceMailCopy = await _mailService
            .GetMailCopyByMessageIdAsync(draftCopy.AssignedAccount.Id, inReplyTo)
            .ConfigureAwait(false);

        if (referenceMailCopy == null)
            return (DraftCreationReason.Empty, null);

        var totalRecipients = mimeMessage.To.Mailboxes.Count() + mimeMessage.Cc.Mailboxes.Count();
        var reason = totalRecipients > 1 ? DraftCreationReason.ReplyAll : DraftCreationReason.Reply;
        return (reason, referenceMailCopy);
    }
}
