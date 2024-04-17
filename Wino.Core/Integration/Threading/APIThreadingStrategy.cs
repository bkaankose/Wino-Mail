using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Services;

namespace Wino.Core.Integration.Threading
{
    public class APIThreadingStrategy : IThreadingStrategy
    {
        private readonly IDatabaseService _databaseService;
        private readonly IFolderService _folderService;

        public APIThreadingStrategy(IDatabaseService databaseService, IFolderService folderService)
        {
            _databaseService = databaseService;
            _folderService = folderService;
        }

        public virtual bool ShouldThreadWithItem(IMailItem originalItem, IMailItem targetItem)
        {
            return originalItem.ThreadId != null && originalItem.ThreadId == targetItem.ThreadId;
        }

        public async Task<List<IMailItem>> ThreadItemsAsync(List<MailCopy> items)
        {
            var accountId = items.First().AssignedAccount.Id;

            var threads = new List<ThreadMailItem>();
            var assignedAccount = items.First().AssignedAccount;

            // TODO: Can be optimized by moving to the caller.
            var sentFolder = await _folderService.GetSpecialFolderByAccountIdAsync(accountId, Domain.Enums.SpecialFolderType.Sent);
            var draftFolder = await _folderService.GetSpecialFolderByAccountIdAsync(accountId, Domain.Enums.SpecialFolderType.Draft);

            if (sentFolder == null || draftFolder == null) return default;

            // Child -> Parent approach.

            var potentialThreadItems = items.Distinct().Where(a => !string.IsNullOrEmpty(a.ThreadId));

            var mailLookupTable = new Dictionary<string, bool>();

            // Fill up the mail lookup table to prevent double thread creation.
            foreach (var mail in items)
                if (!mailLookupTable.ContainsKey(mail.Id))
                    mailLookupTable.Add(mail.Id, false);

            foreach (var potentialItem in potentialThreadItems)
            {
                if (mailLookupTable[potentialItem.Id])
                    continue;

                mailLookupTable[potentialItem.Id] = true;

                var allThreadItems = await GetThreadItemsAsync(potentialItem.ThreadId, accountId, potentialItem.AssignedFolder, sentFolder.Id, draftFolder.Id);

                if (allThreadItems.Count == 1)
                {
                    // It's a single item.
                    // Mark as not-processed as thread.

                    mailLookupTable[potentialItem.Id] = false;
                }
                else
                {
                    // Thread item. Mark all items as true in dict.
                    var threadItem = new ThreadMailItem();

                    foreach (var childThreadItem in allThreadItems)
                    {
                        if (mailLookupTable.ContainsKey(childThreadItem.Id))
                            mailLookupTable[childThreadItem.Id] = true;

                        childThreadItem.AssignedAccount = assignedAccount;
                        childThreadItem.AssignedFolder = await _folderService.GetFolderAsync(childThreadItem.FolderId);

                        threadItem.AddThreadItem(childThreadItem);
                    }

                    // Multiple mail copy ids from different folders are thing for Gmail.
                    if (threadItem.ThreadItems.Count == 1)
                        mailLookupTable[potentialItem.Id] = false;
                    else
                        threads.Add(threadItem);
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

        private async Task<List<MailCopy>> GetThreadItemsAsync(string threadId,
                                                               Guid accountId,
                                                               MailItemFolder threadingFolder,
                                                               Guid sentFolderId,
                                                               Guid draftFolderId)
        {
            // Only items from the folder that we are threading for, sent and draft folder items must be included.
            // This is important because deleted items or item assignments that belongs to different folder is 
            // affecting the thread creation here.

            // If the threading is done from Sent or Draft folder, include everything...

            // TODO: Convert to SQLKata query.

            string query = string.Empty;

            if (threadingFolder.SpecialFolderType == SpecialFolderType.Draft || threadingFolder.SpecialFolderType == SpecialFolderType.Sent)
            {
                query = @$"SELECT DISTINCT MC.* FROM MailCopy MC
                           INNER JOIN MailItemFolder MF on MF.Id = MC.FolderId
                           WHERE MF.MailAccountId == '{accountId}' AND MC.ThreadId = '{threadId}'";
            }
            else
            {
                query = @$"SELECT DISTINCT MC.* FROM MailCopy MC
                           INNER JOIN MailItemFolder MF on MF.Id = MC.FolderId
                           WHERE MF.MailAccountId == '{accountId}' AND MC.FolderId IN ('{threadingFolder.Id}','{sentFolderId}','{draftFolderId}')
                           AND MC.ThreadId = '{threadId}'";
            }


            return await _databaseService.Connection.QueryAsync<MailCopy>(query);
        }
    }
}
