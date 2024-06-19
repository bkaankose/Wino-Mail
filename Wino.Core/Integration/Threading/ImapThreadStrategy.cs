using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SqlKata;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Extensions;
using Wino.Core.Services;

namespace Wino.Core.Integration.Threading
{
    public class ImapThreadStrategy : IThreadingStrategy
    {
        private readonly IDatabaseService _databaseService;
        private readonly IFolderService _folderService;

        public ImapThreadStrategy(IDatabaseService databaseService, IFolderService folderService)
        {
            _databaseService = databaseService;
            _folderService = folderService;
        }

        private Task<MailCopy> GetReplyParentAsync(IMailItem replyItem, Guid accountId, Guid threadingFolderId, Guid sentFolderId, Guid draftFolderId)
        {
            if (string.IsNullOrEmpty(replyItem?.MessageId)) return Task.FromResult<MailCopy>(null);

            var query = new Query("MailCopy")
                .Distinct()
                .Take(1)
                .Join("MailItemFolder", "MailItemFolder.Id", "MailCopy.FolderId")
                .Where("MailItemFolder.MailAccountId", accountId)
                .WhereIn("MailItemFolder.Id", new List<Guid> { threadingFolderId, sentFolderId, draftFolderId })
                .Where("MailCopy.MessageId", replyItem.InReplyTo)
                .WhereNot("MailCopy.Id", replyItem.Id)
                .Select("MailCopy.*");

            return _databaseService.Connection.FindWithQueryAsync<MailCopy>(query.GetRawQuery());
        }

        private Task<MailCopy> GetInReplyToReplyAsync(IMailItem originalItem, Guid accountId, Guid threadingFolderId, Guid sentFolderId, Guid draftFolderId)
        {
            if (string.IsNullOrEmpty(originalItem?.MessageId)) return Task.FromResult<MailCopy>(null);

            var query = new Query("MailCopy")
                .Distinct()
                .Take(1)
                .Join("MailItemFolder", "MailItemFolder.Id", "MailCopy.FolderId")
                .WhereNot("MailCopy.Id", originalItem.Id)
                .Where("MailItemFolder.MailAccountId", accountId)
                .Where("MailCopy.InReplyTo", originalItem.MessageId)
                .WhereIn("MailItemFolder.Id", new List<Guid> { threadingFolderId, sentFolderId, draftFolderId })
                .Select("MailCopy.*");

            var raq = query.GetRawQuery();

            return _databaseService.Connection.FindWithQueryAsync<MailCopy>(query.GetRawQuery());
        }

        public async Task<List<IMailItem>> ThreadItemsAsync(List<MailCopy> items)
        {
            var threads = new List<ThreadMailItem>();

            var account = items.First().AssignedAccount;
            var accountId = account.Id;

            // Child -> Parent approach.

            var mailLookupTable = new Dictionary<string, bool>();

            // Fill up the mail lookup table to prevent double thread creation.
            foreach (var mail in items)
                if (!mailLookupTable.ContainsKey(mail.Id))
                    mailLookupTable.Add(mail.Id, false);

            var sentFolder = await _folderService.GetSpecialFolderByAccountIdAsync(accountId, Domain.Enums.SpecialFolderType.Sent);
            var draftFolder = await _folderService.GetSpecialFolderByAccountIdAsync(accountId, Domain.Enums.SpecialFolderType.Draft);

            // Threading is not possible. Return items as it is.

            if (sentFolder == null || draftFolder == null) return new List<IMailItem>(items);

            foreach (var replyItem in items)
            {
                if (mailLookupTable[replyItem.Id])
                    continue;

                mailLookupTable[replyItem.Id] = true;

                var threadItem = new ThreadMailItem();

                threadItem.AddThreadItem(replyItem);

                var replyToChild = await GetReplyParentAsync(replyItem, accountId, replyItem.AssignedFolder.Id, sentFolder.Id, draftFolder.Id);

                // Build up 
                while (replyToChild != null)
                {
                    replyToChild.AssignedAccount = account;

                    if (replyToChild.FolderId == draftFolder.Id)
                        replyToChild.AssignedFolder = draftFolder;

                    if (replyToChild.FolderId == sentFolder.Id)
                        replyToChild.AssignedFolder = sentFolder;

                    if (replyToChild.FolderId == replyItem.AssignedFolder.Id)
                        replyToChild.AssignedFolder = replyItem.AssignedFolder;

                    threadItem.AddThreadItem(replyToChild);

                    if (mailLookupTable.ContainsKey(replyToChild.Id))
                        mailLookupTable[replyToChild.Id] = true;

                    replyToChild = await GetReplyParentAsync(replyToChild, accountId, replyToChild.AssignedFolder.Id, sentFolder.Id, draftFolder.Id);
                }

                // Build down
                var replyToParent = await GetInReplyToReplyAsync(replyItem, accountId, replyItem.AssignedFolder.Id, sentFolder.Id, draftFolder.Id);

                while (replyToParent != null)
                {
                    replyToParent.AssignedAccount = account;

                    if (replyToParent.FolderId == draftFolder.Id)
                        replyToParent.AssignedFolder = draftFolder;

                    if (replyToParent.FolderId == sentFolder.Id)
                        replyToParent.AssignedFolder = sentFolder;

                    if (replyToParent.FolderId == replyItem.AssignedFolder.Id)
                        replyToParent.AssignedFolder = replyItem.AssignedFolder;

                    threadItem.AddThreadItem(replyToParent);

                    if (mailLookupTable.ContainsKey(replyToParent.Id))
                        mailLookupTable[replyToParent.Id] = true;

                    replyToParent = await GetInReplyToReplyAsync(replyToParent, accountId, replyToParent.AssignedFolder.Id, sentFolder.Id, draftFolder.Id);
                }

                // It's a thread item.

                if (threadItem.ThreadItems.Count > 1 && !threads.Exists(a => a.Id == threadItem.Id))
                {
                    threads.Add(threadItem);
                }
                else
                {
                    // False alert. This is not a thread item.
                    mailLookupTable[replyItem.Id] = false;

                    // TODO: Here potentially check other algorithms for threading like References.
                }
            }

            // At this points all mails in the list belong to single items.
            // Merge with threads.
            // Last sorting will be done later on in MailService.

            // Remove single mails that are included in thread.
            items.RemoveAll(a => mailLookupTable.ContainsKey(a.Id) && mailLookupTable[a.Id]);

            var finalList = new List<IMailItem>(items);

            finalList.AddRange(threads);

            return finalList;
        }

        public bool ShouldThreadWithItem(IMailItem originalItem, IMailItem targetItem)
        {
            bool isChild = originalItem.InReplyTo != null && originalItem.InReplyTo == targetItem.MessageId;
            bool isParent = originalItem.MessageId != null && originalItem.MessageId == targetItem.InReplyTo;

            return isChild || isParent;
        }
    }
}
