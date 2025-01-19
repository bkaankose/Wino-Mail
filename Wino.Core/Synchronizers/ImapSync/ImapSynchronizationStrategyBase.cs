using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Extensions;
using IMailService = Wino.Core.Domain.Interfaces.IMailService;

namespace Wino.Core.Synchronizers.ImapSync
{
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

        protected ImapSynchronizationStrategyBase(IFolderService folderService, IMailService mailService)
        {
            FolderService = folderService;
            MailService = mailService;
        }

        public abstract Task<List<string>> HandleSynchronizationAsync(IImapClient client, MailItemFolder folder, IImapSynchronizer synchronizer, CancellationToken cancellationToken = default);

        protected async Task HandleMessageFlagsChangeAsync(MailItemFolder folder, UniqueId? uniqueId, MessageFlags flags)
        {
            if (folder == null) return;
            if (uniqueId == null) return;

            var localMailCopyId = MailkitClientExtensions.CreateUid(folder.Id, uniqueId.Value.Id);

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

        protected async Task HandleMessageDeletedAsync(MailItemFolder folder, IList<UniqueId> uniqueIds)
        {
            if (folder == null) return;
            if (uniqueIds == null || uniqueIds.Count == 0) return;

            foreach (var uniqueId in uniqueIds)
            {
                if (uniqueId == null) continue;
                var localMailCopyId = MailkitClientExtensions.CreateUid(folder.Id, uniqueId.Id);

                await MailService.DeleteMailAsync(folder.MailAccountId, localMailCopyId).ConfigureAwait(false);
            }
        }

        protected async Task ManageUUIdBasedDeletedMessagesAsync(MailItemFolder localFolder, IMailFolder remoteFolder, CancellationToken cancellationToken = default)
        {
            var allUids = (await FolderService.GetKnownUidsForFolderAsync(localFolder.Id)).Select(a => new UniqueId(a)).ToList();

            if (allUids.Count > 0)
            {
                var remoteAllUids = await remoteFolder.SearchAsync(SearchQuery.All, cancellationToken);
                var deletedUids = allUids.Except(remoteAllUids).ToList();

                await HandleMessageDeletedAsync(localFolder, deletedUids).ConfigureAwait(false);
            }
        }
    }
}
