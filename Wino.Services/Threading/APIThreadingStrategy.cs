using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Services;

namespace Wino.Services.Threading
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

        ///<inheritdoc/>
        public async Task<List<IMailItem>> ThreadItemsAsync(List<MailCopy> items, IMailItemFolder threadingForFolder)
        {
            var assignedAccount = items[0].AssignedAccount;

            var sentFolder = await _folderService.GetSpecialFolderByAccountIdAsync(assignedAccount.Id, SpecialFolderType.Sent);
            var draftFolder = await _folderService.GetSpecialFolderByAccountIdAsync(assignedAccount.Id, SpecialFolderType.Draft);

            if (sentFolder == null || draftFolder == null) return default;

            // True: Non threaded items.
            // False: Potentially threaded items.
            var nonThreadedOrThreadedMails = items
                .Distinct()
                .GroupBy(x => string.IsNullOrEmpty(x.ThreadId))
                .ToDictionary(x => x.Key, x => x);

            _ = nonThreadedOrThreadedMails.TryGetValue(true, out var nonThreadedMails);
            var isThreadedItems = nonThreadedOrThreadedMails.TryGetValue(false, out var potentiallyThreadedMails);

            List<IMailItem> resultList = nonThreadedMails is null ? [] : [.. nonThreadedMails];

            if (isThreadedItems)
            {
                var threadItems = (await GetThreadItemsAsync(potentiallyThreadedMails.Select(x => (x.ThreadId, x.AssignedFolder)).ToList(), assignedAccount.Id, sentFolder.Id, draftFolder.Id))
                .GroupBy(x => x.ThreadId);

                foreach (var threadItem in threadItems)
                {
                    if (threadItem.Count() == 1)
                    {
                        resultList.Add(threadItem.First());
                        continue;
                    }

                    var thread = new ThreadMailItem();

                    foreach (var childThreadItem in threadItem)
                    {
                        if (thread.ThreadItems.Any(a => a.Id == childThreadItem.Id))
                        {
                            // Mail already exist in the thread.
                            // There should be only 1 instance of the mail in the thread.
                            // Make sure we add the correct one.

                            // Add the one with threading folder.
                            var threadingFolderItem = threadItem.FirstOrDefault(a => a.Id == childThreadItem.Id && a.FolderId == threadingForFolder.Id);

                            if (threadingFolderItem == null) continue;

                            // Remove the existing one.
                            thread.ThreadItems.Remove(thread.ThreadItems.First(a => a.Id == childThreadItem.Id));

                            // Add the correct one for listing.
                            thread.AddThreadItem(threadingFolderItem);
                        }
                        else
                        {
                            thread.AddThreadItem(childThreadItem);
                        }
                    }

                    if (thread.ThreadItems.Count > 1)
                    {
                        resultList.Add(thread);
                    }
                    else
                    {
                        // Don't make threads if the thread has only one item.
                        // Gmail has may have multiple assignments for the same item.

                        resultList.Add(thread.ThreadItems.First());
                    }
                }
            }

            return resultList;
        }

        private async Task<List<MailCopy>> GetThreadItemsAsync(List<(string threadId, MailItemFolder threadingFolder)> potentialThread,
                                                               Guid accountId,
                                                               Guid sentFolderId,
                                                               Guid draftFolderId)
        {
            // Only items from the folder that we are threading for, sent and draft folder items must be included.
            // This is important because deleted items or item assignments that belongs to different folder is 
            // affecting the thread creation here.

            // If the threading is done from Sent or Draft folder, include everything...

            // TODO: Convert to SQLKata query.

            var query = @$"SELECT DISTINCT MC.* FROM MailCopy MC
                           INNER JOIN MailItemFolder MF on MF.Id = MC.FolderId
                           WHERE MF.MailAccountId == '{accountId}' AND
                           ({string.Join(" OR ", potentialThread.Select(x => ConditionForItem(x, sentFolderId, draftFolderId)))})";

            return await _databaseService.Connection.QueryAsync<MailCopy>(query);

            static string ConditionForItem((string threadId, MailItemFolder threadingFolder) potentialThread, Guid sentFolderId, Guid draftFolderId)
            {
                if (potentialThread.threadingFolder.SpecialFolderType == SpecialFolderType.Draft || potentialThread.threadingFolder.SpecialFolderType == SpecialFolderType.Sent)
                    return $"(MC.ThreadId = '{potentialThread.threadId}')";

                return $"(MC.ThreadId = '{potentialThread.threadId}' AND MC.FolderId IN ('{potentialThread.threadingFolder.Id}','{sentFolderId}','{draftFolderId}'))";
            }
        }
    }
}
