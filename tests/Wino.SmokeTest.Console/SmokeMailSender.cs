using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.SmokeTest.ConsoleApp;

internal sealed class SmokeMailSender(
    IAccountService accountService,
    IFolderService folderService,
    IMailService mailService,
    IMimeFileService mimeFileService,
    IWinoRequestDelegator requestDelegator,
    SmokeSynchronizationHost synchronizationHost)
{
    public async Task<SmokeSentMessage> SendAsync(
        MailAccount account,
        string recipient,
        string subject,
        string plainText,
        string html,
        string? attachmentPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);

        var alias = await accountService.GetPrimaryAccountAliasAsync(account.Id).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account has no primary sending alias.");
        var draftFolder = await folderService
            .GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Draft)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account has no configured Draft folder.");
        var sentFolder = await folderService
            .GetSpecialFolderByAccountIdAsync(account.Id, SpecialFolderType.Sent)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account has no configured Sent folder.");

        var options = new DraftCreationOptions
        {
            Reason = DraftCreationReason.Empty,
            InitialBodyText = plainText
        };
        var (draftCopy, originalBase64) = await mailService.CreateDraftAsync(account.Id, options).ConfigureAwait(false);
        try
        {
            var mime = originalBase64.GetMimeMessageFromBase64();

            mime.To.Clear();
            mime.Cc.Clear();
            mime.Bcc.Clear();
            mime.To.Add(MailboxAddress.Parse(recipient));
            mime.Subject = subject;
            mime.Date = DateTimeOffset.Now;

            var bodyBuilder = new BodyBuilder
            {
                TextBody = plainText,
                HtmlBody = html
            };

            if (!string.IsNullOrWhiteSpace(attachmentPath))
            {
                var fullAttachmentPath = Path.GetFullPath(attachmentPath);
                if (!File.Exists(fullAttachmentPath))
                    throw new FileNotFoundException("Smoke-test attachment was not found.", fullAttachmentPath);

                var attachmentBytes = await File.ReadAllBytesAsync(fullAttachmentPath, cancellationToken).ConfigureAwait(false);
                await bodyBuilder.Attachments
                    .AddAsync(Path.GetFileName(fullAttachmentPath), new MemoryStream(attachmentBytes))
                    .ConfigureAwait(false);
            }

            mime.Body = bodyBuilder.ToMessageBody();
            var updatedBase64 = mime.GetBase64MimeMessage();

            draftCopy.Subject = subject;
            draftCopy.PreviewText = plainText;
            draftCopy.FromName = alias.AliasSenderName ?? account.SenderName;
            draftCopy.FromAddress = alias.AliasAddress;
            draftCopy.HasAttachments = !string.IsNullOrWhiteSpace(attachmentPath);

            await mailService.UpdateMailAsync(draftCopy).ConfigureAwait(false);
            await mimeFileService.SaveMimeMessageAsync(draftCopy.FileId, mime, account.Id).ConfigureAwait(false);

            var createResult = await synchronizationHost.ExecuteMailOperationAsync(
                account.Id,
                () => requestDelegator.ExecuteAsync(new DraftPreparationRequest(
                    account,
                    draftCopy,
                    updatedBase64,
                    DraftCreationReason.Empty)),
                cancellationToken).ConfigureAwait(false);
            SmokeResultGuard.ThrowIfFailed("Draft upload", createResult.CompletedState, createResult.Exception);

            var mappedDraft = await mailService.GetSingleMailItemAsync(draftCopy.UniqueId).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The uploaded draft could not be reloaded from the database.");

            var sendResult = await synchronizationHost.ExecuteMailOperationAsync(
                account.Id,
                () => requestDelegator.ExecuteAsync(new SendDraftPreparationRequest(
                    mappedDraft,
                    alias,
                    sentFolder,
                    draftFolder,
                    account.Preferences,
                    updatedBase64)),
                cancellationToken).ConfigureAwait(false);
            SmokeResultGuard.ThrowIfFailed("Send", sendResult.CompletedState, sendResult.Exception);

            return new SmokeSentMessage(mime.MessageId, subject, draftCopy.UniqueId);
        }
        catch
        {
            await TryDiscardFailedDraftAsync(account, draftCopy).ConfigureAwait(false);
            throw;
        }
    }

    private async Task TryDiscardFailedDraftAsync(MailAccount account, MailCopy draftCopy)
    {
        try
        {
            var current = await mailService.GetSingleMailItemAsync(draftCopy.UniqueId).ConfigureAwait(false);
            if (current is null)
                return;

            if (current.IsLocalDraft)
            {
                await mailService.DiscardLocalDraftAsync(account.Id, current.UniqueId).ConfigureAwait(false);
                return;
            }

            await synchronizationHost.ExecuteMailOperationAsync(
                account.Id,
                () => requestDelegator.ExecuteAsync(new MailOperationPreperationRequest(
                    MailOperation.HardDelete,
                    current,
                    ignoreHardDeleteProtection: true)),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original send failure. The run report identifies the failed send step.
        }
    }
}

internal sealed record SmokeSentMessage(string MessageId, string Subject, Guid LocalDraftUniqueId);

internal static class SmokeResultGuard
{
    public static void ThrowIfFailed(string operation, SynchronizationCompletedState state, Exception? exception)
    {
        if (state == SynchronizationCompletedState.Success)
            return;

        throw new InvalidOperationException(
            $"{operation} synchronization completed with state {state}.",
            exception);
    }
}
