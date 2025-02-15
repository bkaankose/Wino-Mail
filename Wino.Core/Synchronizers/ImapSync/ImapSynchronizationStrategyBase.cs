using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MoreLinq;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Services.Extensions;
using IMailService = Wino.Core.Domain.Interfaces.IMailService;

namespace Wino.Core.Synchronizers.ImapSync;

public abstract class ImapSynchronizationStrategyBase : IImapSynchronizerStrategy
{
    // Minimum summary items to Fetch for mail synchronization from IMAP.
    protected readonly MessageSummaryItems MailSynchronizationFlags =
        MessageSummaryItems.Flags |
        MessageSummaryItems.UniqueId |
        MessageSummaryItems.ThreadId |
        MessageSummaryItems.EmailId |
        MessageSummaryItems.Headers |
        MessageSummaryItems.PreviewText |
        MessageSummaryItems.GMailThreadId |
        MessageSummaryItems.References |
        MessageSummaryItems.ModSeq;

    protected IFolderService FolderService { get; }
    protected IMailService MailService { get; }
    protected MailItemFolder Folder { get; set; }

    protected ImapSynchronizationStrategyBase(IFolderService folderService, IMailService mailService)
    {
        FolderService = folderService;
        MailService = mailService;
    }

    public abstract Task<List<string>> HandleSynchronizationAsync(IImapClient client, MailItemFolder folder, IImapSynchronizer synchronizer, CancellationToken cancellationToken = default);
    internal abstract Task<IList<UniqueId>> GetChangedUidsAsync(IImapClient client, IMailFolder remoteFolder, IImapSynchronizer synchronizer, CancellationToken cancellationToken = default);

    protected async Task<List<string>> HandleChangedUIdsAsync(IImapSynchronizer synchronizer, IMailFolder remoteFolder, IList<UniqueId> changedUids, CancellationToken cancellationToken)
    {
        List<string> downloadedMessageIds = new();

        var existingMails = await MailService.GetExistingMailsAsync(Folder.Id, changedUids).ConfigureAwait(false);
        var existingMailUids = existingMails.Select(m => MailkitClientExtensions.ResolveUidStruct(m.Id)).ToArray();

        // These are the non-existing mails. They will be downloaded + processed.
        var newMessageIds = changedUids.Except(existingMailUids).ToList();
        var deletedMessageIds = existingMailUids.Except(changedUids).ToList();

        // Fetch minimum data for the existing mails in one query.
        var existingFlagData = await remoteFolder.FetchAsync(existingMailUids, MessageSummaryItems.Flags | MessageSummaryItems.UniqueId).ConfigureAwait(false);

        foreach (var update in existingFlagData)
        {
            if (update.UniqueId == null)
            {
                Log.Warning($"Couldn't fetch UniqueId for the mail. FetchAsync failed.");
                continue;
            }

            if (update.Flags == null)
            {
                Log.Warning($"Couldn't fetch flags for the mail with UID {update.UniqueId.Id}. FetchAsync failed.");
                continue;
            }

            var existingMail = existingMails.FirstOrDefault(m => MailkitClientExtensions.ResolveUidStruct(m.Id).Id == update.UniqueId.Id);

            if (existingMail == null)
            {
                Log.Warning($"Couldn't find the mail with UID {update.UniqueId.Id} in the local database. Flag update is ignored.");
                continue;
            }

            await HandleMessageFlagsChangeAsync(existingMail, update.Flags.Value).ConfigureAwait(false);
        }

        // Fetch the new mails in batch.

        var batchedMessageIds = newMessageIds.Batch(50);

        foreach (var group in batchedMessageIds)
        {
            var summaries = await remoteFolder.FetchAsync(group, MailSynchronizationFlags, cancellationToken).ConfigureAwait(false);

            foreach (var summary in summaries)
            {
                var mimeMessage = await remoteFolder.GetMessageAsync(summary.UniqueId, cancellationToken).ConfigureAwait(false);

                var creationPackage = new ImapMessageCreationPackage(summary, mimeMessage);

                var mailPackages = await synchronizer.CreateNewMailPackagesAsync(creationPackage, Folder, cancellationToken).ConfigureAwait(false);

                if (mailPackages != null)
                {
                    foreach (var package in mailPackages)
                    {
                        // Local draft is mapped. We don't need to create a new mail copy.
                        if (package == null) continue;

                        bool isCreatedNew = await MailService.CreateMailAsync(Folder.MailAccountId, package).ConfigureAwait(false);

                        // This is upsert. We are not interested in updated mails.
                        if (isCreatedNew) downloadedMessageIds.Add(package.Copy.Id);
                    }
                }
            }
        }


        return downloadedMessageIds;
    }

    protected async Task HandleMessageFlagsChangeAsync(UniqueId? uniqueId, MessageFlags flags)
    {
        if (Folder == null) return;
        if (uniqueId == null) return;

        var localMailCopyId = MailkitClientExtensions.CreateUid(Folder.Id, uniqueId.Value.Id);

        var isFlagged = MailkitClientExtensions.GetIsFlagged(flags);
        var isRead = MailkitClientExtensions.GetIsRead(flags);

        await MailService.ChangeReadStatusAsync(localMailCopyId, isRead).ConfigureAwait(false);
        await MailService.ChangeFlagStatusAsync(localMailCopyId, isFlagged).ConfigureAwait(false);
    }

    protected async Task HandleMessageFlagsChangeAsync(MailCopy mailCopy, MessageFlags flags)
    {
        if (mailCopy == null) return;

        var isFlagged = MailkitClientExtensions.GetIsFlagged(flags);
        var isRead = MailkitClientExtensions.GetIsRead(flags);

        if (isFlagged != mailCopy.IsFlagged)
        {
            await MailService.ChangeFlagStatusAsync(mailCopy.Id, isFlagged).ConfigureAwait(false);
        }

        if (isRead != mailCopy.IsRead)
        {
            await MailService.ChangeReadStatusAsync(mailCopy.Id, isRead).ConfigureAwait(false);
        }
    }

    protected async Task HandleMessageDeletedAsync(IList<UniqueId> uniqueIds)
    {
        if (Folder == null) return;
        if (uniqueIds == null || uniqueIds.Count == 0) return;

        foreach (var uniqueId in uniqueIds)
        {
            if (uniqueId == null) continue;
            var localMailCopyId = MailkitClientExtensions.CreateUid(Folder.Id, uniqueId.Id);

            await MailService.DeleteMailAsync(Folder.MailAccountId, localMailCopyId).ConfigureAwait(false);
        }
    }

    protected void OnMessagesVanished(object sender, MessagesVanishedEventArgs args)
        => HandleMessageDeletedAsync(args.UniqueIds).ConfigureAwait(false);

    protected void OnMessageFlagsChanged(object sender, MessageFlagsChangedEventArgs args)
        => HandleMessageFlagsChangeAsync(args.UniqueId, args.Flags).ConfigureAwait(false);

    protected async Task ManageUUIdBasedDeletedMessagesAsync(MailItemFolder localFolder, IMailFolder remoteFolder, CancellationToken cancellationToken = default)
    {
        var allUids = (await FolderService.GetKnownUidsForFolderAsync(localFolder.Id)).Select(a => new UniqueId(a)).ToList();

        if (allUids.Count > 0)
        {
            var remoteAllUids = await remoteFolder.SearchAsync(SearchQuery.All, cancellationToken);
            var deletedUids = allUids.Except(remoteAllUids).ToList();

            await HandleMessageDeletedAsync(deletedUids).ConfigureAwait(false);
        }
    }
}
