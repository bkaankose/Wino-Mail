using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using MailKit;
using MimeKit;
using Serilog;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Helpers;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Folder;
using Wino.Core.Requests.Mail;
using WinoMailService = Wino.Core.Domain.Interfaces.IMailService;

namespace Wino.Core.Synchronizers.Mail;

public sealed class Pop3Synchronizer
    : WinoSynchronizer<Pop3Request, MimeMessage, object, AccountContact>, IPop3Synchronizer
{
    private readonly IPop3ClientFactory _clientFactory;
    private readonly IPop3PersistenceService _persistenceService;
    private readonly WinoMailService _mailService;
    private readonly IFolderService _folderService;
    private readonly IAccountService _accountService;
    private readonly ISmtpTransport _smtpTransport;
    private readonly IMimeFileService _mimeFileService;
    private readonly ILogger _logger = Log.ForContext<Pop3Synchronizer>();

    public Pop3Synchronizer(
        MailAccount account,
        IPop3ClientFactory clientFactory,
        IPop3PersistenceService persistenceService,
        WinoMailService mailService,
        IFolderService folderService,
        IAccountService accountService,
        ISmtpTransport smtpTransport,
        IMimeFileService mimeFileService)
        : base(account, WeakReferenceMessenger.Default)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _persistenceService = persistenceService ?? throw new ArgumentNullException(nameof(persistenceService));
        _mailService = mailService ?? throw new ArgumentNullException(nameof(mailService));
        _folderService = folderService ?? throw new ArgumentNullException(nameof(folderService));
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
        _smtpTransport = smtpTransport ?? throw new ArgumentNullException(nameof(smtpTransport));
        _mimeFileService = mimeFileService ?? throw new ArgumentNullException(nameof(mimeFileService));
    }

    public override uint BatchModificationSize => 1000;
    public override uint InitialMessageDownloadCountPerFolder => 500;

    public override Task<List<NewMailItemPackage>> CreateNewMailPackagesAsync(
        MimeMessage message,
        MailItemFolder assignedFolder,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new List<NewMailItemPackage>
        {
            new(CreateMailPackage(message, assignedFolder, null).Copy, message, assignedFolder.RemoteFolderId)
        });

    public override async Task ExecuteNativeRequestsAsync(
        List<IRequestBundle<Pop3Request>> batchedRequests,
        CancellationToken cancellationToken = default)
    {
        ApplyOptimisticUiChanges(batchedRequests);

        foreach (var bundle in batchedRequests)
        {
            try
            {
                await bundle.NativeRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                RequestUiChangeCoordinator.RevertBundle(bundle);
                throw;
            }
            finally
            {
                RequestUiChangeCoordinator.CompleteRequests([bundle.Request]);
            }
        }
    }

    public override List<IRequestBundle<Pop3Request>> MarkRead(BatchMarkReadRequest requests)
        => CreateBundle(async cancellationToken =>
        {
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _mailService.ChangeReadStatusAsync(request.Item.Id, request.IsRead).ConfigureAwait(false);
            }
        }, requests.FirstOrDefault(), requests);

    public override List<IRequestBundle<Pop3Request>> ChangeFlag(BatchChangeFlagRequest requests)
        => CreateBundle(async cancellationToken =>
        {
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _mailService.ChangeFlagStatusAsync(request.Item.Id, request.IsFlagged).ConfigureAwait(false);
            }
        }, requests.FirstOrDefault(), requests);

    public override List<IRequestBundle<Pop3Request>> Move(BatchMoveRequest requests)
        => CreateBundle(async cancellationToken =>
        {
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _mailService.CreateAssignmentAsync(Account.Id, request.Item.Id, request.ToFolder.RemoteFolderId).ConfigureAwait(false);
                await _mailService.DeleteAssignmentAsync(Account.Id, request.Item.Id, request.FromFolder.RemoteFolderId).ConfigureAwait(false);
            }
        }, requests.FirstOrDefault(), requests);

    public override List<IRequestBundle<Pop3Request>> Archive(BatchArchiveRequest requests)
        => Move(new BatchMoveRequest(requests.Select(request =>
            new MoveRequest(request.Item, request.FromFolder, request.ToFolder))));

    public override List<IRequestBundle<Pop3Request>> Delete(BatchDeleteRequest requests)
        => CreateBundle(async cancellationToken =>
        {
            foreach (var request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!string.IsNullOrWhiteSpace(request.Item.Pop3Uidl))
                {
                    await _persistenceService
                        .AddPendingDeletionAsync(Account.Id, request.Item.Pop3Uidl)
                        .ConfigureAwait(false);
                }

                await _mailService.DeleteMailAsync(Account.Id, request.Item.Id).ConfigureAwait(false);
            }
        }, requests.FirstOrDefault(), requests);

    public override List<IRequestBundle<Pop3Request>> CreateDraft(CreateDraftRequest request)
        => CreateBundle(_ => Task.CompletedTask, request, request);

    public override List<IRequestBundle<Pop3Request>> SendDraft(SendDraftRequest request)
        => CreateBundle(async cancellationToken =>
        {
            var preparation = request.Request;
            var acceptedMessage = await _smtpTransport
                .SendAsync(Account, preparation.Mime, cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var sentFolder = preparation.SentFolder
                                 ?? await _folderService.GetSpecialFolderByAccountIdAsync(Account.Id, SpecialFolderType.Sent).ConfigureAwait(false);
                if (sentFolder == null)
                    throw new InvalidOperationException("The local POP3 Sent folder is missing.");

                var sentPackage = CreateMailPackage(acceptedMessage, sentFolder, null);
                sentPackage.Copy.IsRead = true;
                await _mailService.CreateMailRawAsync(Account, sentFolder, sentPackage).ConfigureAwait(false);
                await _mailService.DeleteMailAsync(Account.Id, preparation.MailItem.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // SMTP acceptance is the commit point. Never surface a local-finalization failure
                // as a retryable send because that can duplicate a message.
                _logger.Error(ex,
                    "SMTP accepted POP3 draft {MailId}, but local Sent finalization failed for account {AccountId}.",
                    preparation.MailItem.Id,
                    Account.Id);
            }
        }, request, request);

    public override List<IRequestBundle<Pop3Request>> EmptyFolder(EmptyFolderRequest request)
        => Delete(new BatchDeleteRequest(request.MailsToDelete.Select(mail => new DeleteRequest(mail))));

    public override List<IRequestBundle<Pop3Request>> MarkFolderAsRead(MarkFolderAsReadRequest request)
        => MarkRead(new BatchMarkReadRequest(request.MailsToMarkRead.Select(mail => new MarkReadRequest(mail, true))));

    protected override async Task<MailSynchronizationResult> SynchronizeMailsInternalAsync(
        MailSynchronizationOptions options,
        CancellationToken cancellationToken = default)
    {
        var inbox = await _folderService
            .GetSpecialFolderByAccountIdAsync(Account.Id, SpecialFolderType.Inbox)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The local POP3 Inbox is missing.");

        using var client = _clientFactory.Create(Account.Id, Account.IsProtocolLogEnabled);
        var committedDeletionIds = new List<Guid>();
        var attemptedDeletions = new List<Pop3PendingServerDeletion>();
        var downloaded = new List<MailCopy>();
        var issues = new List<SynchronizationIssue>();
        var cleanDisconnect = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await client.ConnectAndAuthenticateAsync(Account.ServerInformation, cancellationToken).ConfigureAwait(false);
            if (!client.SupportsUids)
                throw new NotSupportedException("POP3 synchronization requires the UIDL capability.");

            var uidls = await client.GetMessageUidsAsync(cancellationToken).ConfigureAwait(false);
            ValidateUidls(uidls, client.Count);

            var indexByUidl = uidls
                .Select((uidl, index) => (uidl, index))
                .GroupBy(item => item.uidl, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
            var tombstones = await _persistenceService.GetPendingDeletionsAsync(Account.Id).ConfigureAwait(false);
            var pendingUidls = tombstones.Select(item => item.Uidl).ToHashSet(StringComparer.Ordinal);

            foreach (var tombstone in tombstones)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attemptedDeletions.Add(tombstone);

                if (indexByUidl.TryGetValue(tombstone.Uidl, out var messageIndex))
                    await client.DeleteMessageAsync(messageIndex, cancellationToken).ConfigureAwait(false);

                committedDeletionIds.Add(tombstone.Id);
            }

            var knownUidls = await _persistenceService.GetKnownUidlsAsync(Account.Id).ConfigureAwait(false);
            var initialImport = string.IsNullOrWhiteSpace(Account.SynchronizationDeltaIdentifier);
            var cutoff = initialImport
                ? Account.InitialSynchronizationRange.ToCutoffDateUtc(DateTime.UtcNow)
                : null;
            var candidates = indexByUidl
                .Where(item => !knownUidls.Contains(item.Key) && !pendingUidls.Contains(item.Key))
                .OrderBy(item => item.Value)
                .ToList();

            UpdateSyncProgress(candidates.Count, candidates.Count, "Downloading POP3 messages...");

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (cutoff.HasValue)
                    {
                        var headers = await client.GetMessageHeadersAsync(candidate.Value, cancellationToken).ConfigureAwait(false);
                        if (TryGetMessageDate(headers, out var messageDate) && messageDate < cutoff.Value)
                        {
                            await _persistenceService.MarkUidlKnownAsync(Account.Id, candidate.Key).ConfigureAwait(false);
                            UpdateSyncProgress(candidates.Count, --RemainingItemsToSync, "Downloading POP3 messages...");
                            continue;
                        }
                    }

                    var message = await client.GetMessageAsync(candidate.Value, cancellationToken).ConfigureAwait(false);
                    var package = CreateMailPackage(message, inbox, candidate.Key);
                    if (await _mailService.CreateMailAsync(Account.Id, package).ConfigureAwait(false))
                        downloaded.Add(package.Copy);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "POP3 message import failed for UIDL {Uidl} in account {AccountId}.", candidate.Key, Account.Id);
                    issues.Add(SynchronizationIssue.FromException(ex, "POP3MessageImport"));
                }

                UpdateSyncProgress(candidates.Count, --RemainingItemsToSync, "Downloading POP3 messages...");
            }

            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            cleanDisconnect = true;
            await _persistenceService.RemovePendingDeletionsAsync(committedDeletionIds).ConfigureAwait(false);

            if (initialImport)
            {
                Account.SynchronizationDeltaIdentifier = "pop3-uidl-v1";
                await _accountService.UpdateAccountAsync(Account).ConfigureAwait(false);
            }

            var result = MailSynchronizationResult.Completed(downloaded);
            return result.MergeIssues(issues);
        }
        catch
        {
            foreach (var tombstone in attemptedDeletions)
            {
                await _persistenceService
                    .MarkDeletionAttemptFailedAsync(tombstone.Id, "POP3 session did not commit cleanly.")
                    .ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (client.IsConnected && !cleanDisconnect)
            {
                try
                {
                    await client.DisconnectAsync(false, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "POP3 rollback disconnect failed for account {AccountId}.", Account.Id);
                }
            }

            ResetSyncProgress();
        }
    }

    protected override Task<CalendarSynchronizationResult> SynchronizeCalendarEventsInternalAsync(
        CalendarSynchronizationOptions options,
        CancellationToken cancellationToken = default)
        => Task.FromResult(CalendarSynchronizationResult.Empty);

    public override async Task DownloadMissingMimeMessageAsync(
        MailCopy mailItem,
        ITransferProgress transferProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mailItem?.Pop3Uidl))
            throw new InvalidOperationException("The local message has no POP3 UIDL.");

        using var client = _clientFactory.Create(Account.Id, Account.IsProtocolLogEnabled);
        var cleanDisconnect = false;

        try
        {
            await client.ConnectAndAuthenticateAsync(Account.ServerInformation, cancellationToken).ConfigureAwait(false);
            if (!client.SupportsUids)
                throw new NotSupportedException("POP3 synchronization requires the UIDL capability.");

            var uidls = await client.GetMessageUidsAsync(cancellationToken).ConfigureAwait(false);
            ValidateUidls(uidls, client.Count);
            var index = uidls.ToList().FindIndex(uidl => string.Equals(uidl, mailItem.Pop3Uidl, StringComparison.Ordinal));
            if (index < 0)
                throw new InvalidOperationException("The POP3 message is no longer available on the server.");

            var message = await client.GetMessageAsync(index, cancellationToken).ConfigureAwait(false);
            if (!await _mimeFileService.SaveMimeMessageAsync(mailItem.FileId, message, Account.Id).ConfigureAwait(false))
                throw new IOException("The POP3 message MIME file could not be saved.");

            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
            cleanDisconnect = true;
        }
        finally
        {
            if (client.IsConnected && !cleanDisconnect)
            {
                try
                {
                    await client.DisconnectAsync(false, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "POP3 rollback disconnect failed for account {AccountId}.", Account.Id);
                }
            }
        }
    }

    public override Task<List<MailCopy>> OnlineSearchAsync(
        RemoteMailSearchCriteria criteria,
        List<IMailItemFolder> folders,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("POP3 accounts use local search.");

    private static List<IRequestBundle<Pop3Request>> CreateBundle(
        Func<CancellationToken, Task> action,
        IRequestBase request,
        IUIChangeRequest uiChangeRequest)
    {
        if (request == null)
            return [];

        return [new Pop3RequestBundle(new Pop3Request(action), request, uiChangeRequest)];
    }

    private static void ValidateUidls(IReadOnlyList<string> uidls, int messageCount)
    {
        if (uidls == null || uidls.Count != messageCount || uidls.Any(string.IsNullOrWhiteSpace))
            throw new NotSupportedException("The POP3 server returned an incomplete UIDL list.");

        if (uidls.Distinct(StringComparer.Ordinal).Count() != uidls.Count)
            throw new InvalidOperationException("The POP3 server returned duplicate UIDL values.");
    }

    private static bool TryGetMessageDate(HeaderList headers, out DateTime utcDate)
    {
        utcDate = default;
        var rawDate = headers?[HeaderId.Date];
        if (!DateTimeOffset.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return false;

        utcDate = parsed.UtcDateTime;
        return true;
    }

    private static NewMailItemPackage CreateMailPackage(MimeMessage message, MailItemFolder folder, string uidl)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(folder);

        var mailbox = message.From?.Mailboxes?.FirstOrDefault() ?? message.Sender;
        var messageId = NormalizeMessageId(message.MessageId);
        var references = message.References == null ? string.Empty : string.Join(';', message.References);
        var inReplyTo = NormalizeMessageId(message.InReplyTo);
        var threadId = message.References?.FirstOrDefault() ?? inReplyTo ?? messageId;
        var stableId = string.IsNullOrWhiteSpace(uidl)
            ? $"local-{Guid.NewGuid():N}"
            : $"pop3-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{folder.MailAccountId:N}\n{uidl}")))}";
        var copy = new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = stableId,
            FolderId = folder.Id,
            AssignedFolder = folder,
            Pop3Uidl = uidl,
            CreationDate = message.Date == default ? DateTime.UtcNow : message.Date.UtcDateTime,
            ThreadId = threadId ?? string.Empty,
            MessageId = messageId ?? string.Empty,
            Subject = message.Subject ?? string.Empty,
            PreviewText = message.TextBody ?? message.Subject ?? string.Empty,
            FromAddress = mailbox?.Address ?? string.Empty,
            FromName = string.IsNullOrWhiteSpace(mailbox?.Name) ? mailbox?.Address ?? string.Empty : mailbox.Name,
            IsRead = false,
            IsFlagged = false,
            IsFocused = false,
            Importance = MailImportance.Normal,
            References = references,
            InReplyTo = inReplyTo ?? string.Empty,
            HasAttachments = message.Attachments?.Any() == true,
            FileId = Guid.NewGuid(),
            IsDraft = folder.SpecialFolderType == SpecialFolderType.Draft
        };

        return new NewMailItemPackage(copy, message, folder.RemoteFolderId);
    }

    private static string NormalizeMessageId(string value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Trim('<', '>');
}
