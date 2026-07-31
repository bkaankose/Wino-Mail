using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.UI;

namespace Wino.Services;

public class SentMailReceiptService(
    IDatabaseService databaseService,
    IFolderService folderService,
    IAccountService accountService) : BaseDatabaseService(databaseService), ISentMailReceiptService
{
    public async Task PopulateReceiptStateAsync(MailCopy mailCopy)
    {
        if (mailCopy == null)
            return;

        var state = await Connection.FindAsync<SentMailReceiptState>(mailCopy.UniqueId).ConfigureAwait(false);
        ApplyState(mailCopy, state);
    }

    public async Task PopulateReceiptStatesAsync(IReadOnlyCollection<MailCopy> mailCopies)
    {
        if (mailCopies == null || mailCopies.Count == 0)
            return;

        var uniqueIds = mailCopies
            .Select(m => m.UniqueId)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (uniqueIds.Count == 0)
            return;

        var placeholders = string.Join(",", uniqueIds.Select(_ => "?"));
        var states = await Connection.QueryAsync<SentMailReceiptState>(
            $"SELECT * FROM {nameof(SentMailReceiptState)} WHERE {nameof(SentMailReceiptState.MailUniqueId)} IN ({placeholders})",
            uniqueIds.Cast<object>().ToArray()).ConfigureAwait(false);

        var stateLookup = states.ToDictionary(s => s.MailUniqueId);

        foreach (var mailCopy in mailCopies)
        {
            stateLookup.TryGetValue(mailCopy.UniqueId, out var state);
            ApplyState(mailCopy, state);
        }
    }

    public async Task TrackSentMailAsync(MailCopy mailCopy, MimeKit.MimeMessage mimeMessage = null)
    {
        if (mailCopy?.AssignedFolder == null || mailCopy.AssignedAccount == null)
            return;

        if (mailCopy.AssignedFolder.SpecialFolderType != SpecialFolderType.Sent)
            return;

        var isRequested = mailCopy.IsReadReceiptRequested || mimeMessage.HasReadReceiptRequest();
        if (!isRequested || string.IsNullOrWhiteSpace(mailCopy.MessageId))
            return;

        var existing = await Connection.FindAsync<SentMailReceiptState>(mailCopy.UniqueId).ConfigureAwait(false);

        if (existing == null)
        {
            existing = new SentMailReceiptState
            {
                MailUniqueId = mailCopy.UniqueId,
                AccountId = mailCopy.AssignedAccount.Id,
                MessageId = MailHeaderExtensions.NormalizeMessageId(mailCopy.MessageId),
                IsReceiptRequested = true,
                RequestedAtUtc = mailCopy.CreationDate == default ? DateTime.UtcNow : mailCopy.CreationDate,
                Status = SentMailReceiptStatus.Requested
            };

            await Connection.InsertAsync(existing, typeof(SentMailReceiptState)).ConfigureAwait(false);
        }
        else
        {
            existing.AccountId = mailCopy.AssignedAccount.Id;
            existing.MessageId = MailHeaderExtensions.NormalizeMessageId(mailCopy.MessageId);
            existing.IsReceiptRequested = true;
            if (existing.Status == SentMailReceiptStatus.None)
                existing.Status = SentMailReceiptStatus.Requested;

            await Connection.UpdateAsync(existing, typeof(SentMailReceiptState)).ConfigureAwait(false);
        }

        ApplyState(mailCopy, existing);
        ReportUIChange(new MailUpdatedMessage(mailCopy, EntityUpdateSource.Server, MailCopyChangeFlags.ReadReceiptState));
    }

    public async Task ProcessIncomingReceiptAsync(MailCopy receiptMail, MimeKit.MimeMessage mimeMessage)
    {
        if (receiptMail?.AssignedAccount == null || mimeMessage == null)
            return;

        var parsedReceipt = mimeMessage.ParseReadReceipt();
        if (!parsedReceipt.IsReadReceipt || string.IsNullOrWhiteSpace(parsedReceipt.OriginalMessageId))
            return;

        var targetMail = await Connection.FindWithQueryAsync<MailCopy>(
            "SELECT MailCopy.* FROM MailCopy " +
            "INNER JOIN MailItemFolder ON MailCopy.FolderId = MailItemFolder.Id " +
            "WHERE MailItemFolder.MailAccountId = ? AND MailCopy.MessageId = ? AND MailItemFolder.SpecialFolderType = ? " +
            "ORDER BY MailCopy.CreationDate DESC LIMIT 1",
            receiptMail.AssignedAccount.Id,
            parsedReceipt.OriginalMessageId,
            SpecialFolderType.Sent).ConfigureAwait(false);

        if (targetMail == null)
            return;

        var state = await Connection.FindAsync<SentMailReceiptState>(targetMail.UniqueId).ConfigureAwait(false)
            ?? new SentMailReceiptState
            {
                MailUniqueId = targetMail.UniqueId,
                AccountId = receiptMail.AssignedAccount.Id,
                MessageId = parsedReceipt.OriginalMessageId,
                RequestedAtUtc = targetMail.CreationDate == default ? DateTime.UtcNow : targetMail.CreationDate,
                IsReceiptRequested = true,
                Status = SentMailReceiptStatus.Requested
            };

        state.AccountId = receiptMail.AssignedAccount.Id;
        state.MessageId = parsedReceipt.OriginalMessageId;
        state.IsReceiptRequested = true;
        state.Status = SentMailReceiptStatus.Acknowledged;
        state.AcknowledgedAtUtc = parsedReceipt.AcknowledgedAtUtc ?? DateTime.UtcNow;
        state.ReceiptMessageUniqueId = receiptMail.UniqueId;

        if (await Connection.FindAsync<SentMailReceiptState>(state.MailUniqueId).ConfigureAwait(false) == null)
            await Connection.InsertAsync(state, typeof(SentMailReceiptState)).ConfigureAwait(false);
        else
            await Connection.UpdateAsync(state, typeof(SentMailReceiptState)).ConfigureAwait(false);

        var folder = await folderService.GetFolderAsync(targetMail.FolderId).ConfigureAwait(false);
        if (folder == null)
            return;

        var account = await accountService.GetAccountAsync(folder.MailAccountId).ConfigureAwait(false);
        targetMail.AssignedFolder = folder;
        targetMail.AssignedAccount = account;
        ApplyState(targetMail, state);

        ReportUIChange(new MailUpdatedMessage(targetMail, EntityUpdateSource.Server, MailCopyChangeFlags.ReadReceiptState));
    }

    private static void ApplyState(MailCopy mailCopy, SentMailReceiptState state)
    {
        mailCopy.IsReadReceiptRequested = state?.IsReceiptRequested ?? false;
        mailCopy.ReadReceiptStatus = state?.Status ?? SentMailReceiptStatus.None;
        mailCopy.ReadReceiptAcknowledgedAtUtc = state?.AcknowledgedAtUtc;
        mailCopy.ReadReceiptMessageUniqueId = state?.ReceiptMessageUniqueId;
    }
}
