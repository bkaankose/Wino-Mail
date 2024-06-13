using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MoreLinq;
using Serilog;
using SqlKata;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Extensions;
using Wino.Core.Requests;

namespace Wino.Core.Services
{
    public class FolderService : BaseDatabaseService, IFolderService
    {
        private readonly IAccountService _accountService;
        private readonly IMimeFileService _mimeFileService;
        private readonly ILogger _logger = Log.ForContext<FolderService>();

        private readonly SpecialFolderType[] gmailCategoryFolderTypes =
        [
            SpecialFolderType.Promotions,
            SpecialFolderType.Social,
            SpecialFolderType.Updates,
            SpecialFolderType.Forums,
            SpecialFolderType.Personal
        ];

        public FolderService(IDatabaseService databaseService,
                               IAccountService accountService,
                               IMimeFileService mimeFileService) : base(databaseService)
        {
            _accountService = accountService;
            _mimeFileService = mimeFileService;
        }

        public async Task ChangeStickyStatusAsync(Guid folderId, bool isSticky)
            => await Connection.ExecuteAsync("UPDATE MailItemFolder SET IsSticky = ? WHERE Id = ?", isSticky, folderId);

        public async Task<int> GetFolderNotificationBadgeAsync(Guid folderId)
        {
            var folder = await GetFolderAsync(folderId);

            if (folder == null || !folder.ShowUnreadCount) return default;

            var account = await _accountService.GetAccountAsync(folder.MailAccountId);

            if (account == null) return default;

            var query = new Query("MailCopy")
                        .Where("FolderId", folderId)
                        .SelectRaw("count (DISTINCT Id)");

            // If focused inbox is enabled, we need to check if this is the inbox folder.
            if (account.Preferences.IsFocusedInboxEnabled.GetValueOrDefault() && folder.SpecialFolderType == SpecialFolderType.Inbox)
            {
                query.Where("IsFocused", 1);
            }

            // Draft and Junk folders are not counted as unread. They must return the item count instead.
            if (folder.SpecialFolderType != SpecialFolderType.Draft && folder.SpecialFolderType != SpecialFolderType.Junk)
            {
                query.Where("IsRead", 0);
            }

            return await Connection.ExecuteScalarAsync<int>(query.GetRawQuery());
        }

        public async Task<AccountFolderTree> GetFolderStructureForAccountAsync(Guid accountId, bool includeHiddenFolders)
        {
            var account = await _accountService.GetAccountAsync(accountId);

            if (account == null)
                throw new ArgumentException(nameof(account));

            var accountTree = new AccountFolderTree(account);

            // Account folders.
            var folderQuery = Connection.Table<MailItemFolder>().Where(a => a.MailAccountId == accountId);

            if (!includeHiddenFolders)
                folderQuery = folderQuery.Where(a => !a.IsHidden);

            // Load child folders for each folder.
            var allFolders = await folderQuery.OrderBy(a => a.SpecialFolderType).ToListAsync();

            if (allFolders.Any())
            {
                // Get sticky folders. Category type is always sticky.
                // Sticky folders don't have tree structure. So they can be added to the main tree.
                var stickyFolders = allFolders.Where(a => a.IsSticky && a.SpecialFolderType != SpecialFolderType.Category);

                foreach (var stickyFolder in stickyFolders)
                {
                    var childStructure = await GetChildFolderItemsRecursiveAsync(stickyFolder.Id, accountId);

                    accountTree.Folders.Add(childStructure);
                }

                // Check whether we need special 'Categories' kind of folder.
                var categoryExists = allFolders.Any(a => a.SpecialFolderType == SpecialFolderType.Category);

                if (categoryExists)
                {
                    var categoryFolder = allFolders.First(a => a.SpecialFolderType == SpecialFolderType.Category);

                    // Construct category items under pinned items.
                    var categoryFolders = allFolders.Where(a => gmailCategoryFolderTypes.Contains(a.SpecialFolderType));

                    foreach (var categoryFolderSubItem in categoryFolders)
                    {
                        categoryFolder.ChildFolders.Add(categoryFolderSubItem);
                    }

                    accountTree.Folders.Add(categoryFolder);
                    allFolders.Remove(categoryFolder);
                }

                // Move rest of the items into virtual More folder if any.
                var nonStickyFolders = allFolders.Except(stickyFolders);

                if (nonStickyFolders.Any())
                {
                    var virtualMoreFolder = new MailItemFolder()
                    {
                        FolderName = Translator.More,
                        SpecialFolderType = SpecialFolderType.More
                    };

                    foreach (var unstickyItem in nonStickyFolders)
                    {
                        if (account.ProviderType == MailProviderType.Gmail)
                        {
                            // Gmail requires this check to not include child folders as 
                            // separate folder without their parent for More folder...

                            if (!string.IsNullOrEmpty(unstickyItem.ParentRemoteFolderId))
                                continue;
                        }
                        else if (account.ProviderType == MailProviderType.Outlook || account.ProviderType == MailProviderType.Office365)
                        {
                            bool belongsToExistingParent = (await Connection
                                .Table<MailItemFolder>()
                                .Where(a => unstickyItem.ParentRemoteFolderId == a.RemoteFolderId)
                                .CountAsync()) > 0;

                            // No need to include this as unsticky.
                            if (belongsToExistingParent) continue;
                        }

                        var structure = await GetChildFolderItemsRecursiveAsync(unstickyItem.Id, accountId);

                        virtualMoreFolder.ChildFolders.Add(structure);
                    }

                    // Only add more if there are any.
                    if (virtualMoreFolder.ChildFolders.Count > 0)
                        accountTree.Folders.Add(virtualMoreFolder);
                }
            }

            return accountTree;
        }

        private async Task<MailItemFolder> GetChildFolderItemsRecursiveAsync(Guid folderId, Guid accountId)
        {
            var folder = await Connection.Table<MailItemFolder>().Where(a => a.Id == folderId && a.MailAccountId == accountId).FirstOrDefaultAsync();

            if (folder == null)
                return null;

            var childFolders = await Connection.Table<MailItemFolder>()
                .Where(a => a.ParentRemoteFolderId == folder.RemoteFolderId && a.MailAccountId == folder.MailAccountId)
                .ToListAsync();

            foreach (var childFolder in childFolders)
            {
                var subChild = await GetChildFolderItemsRecursiveAsync(childFolder.Id, accountId);
                folder.ChildFolders.Add(subChild);
            }

            return folder;
        }

        public async Task<MailItemFolder> GetSpecialFolderByAccountIdAsync(Guid accountId, SpecialFolderType type)
            => await Connection.Table<MailItemFolder>().FirstOrDefaultAsync(a => a.MailAccountId == accountId && a.SpecialFolderType == type);

        public async Task<MailItemFolder> GetFolderAsync(Guid folderId)
            => await Connection.Table<MailItemFolder>().FirstOrDefaultAsync(a => a.Id.Equals(folderId));

        public Task<int> GetCurrentItemCountForFolder(Guid folderId)
            => Connection.Table<MailCopy>().Where(a => a.FolderId == folderId).CountAsync();

        public Task<List<MailItemFolder>> GetFoldersAsync(Guid accountId)
            => Connection.Table<MailItemFolder>().Where(a => a.MailAccountId == accountId).ToListAsync();

        public async Task UpdateCustomServerMailListAsync(Guid accountId, List<MailItemFolder> folders)
        {
            var account = await Connection.Table<MailAccount>().FirstOrDefaultAsync(a => a.Id == accountId);

            if (account == null)
                return;

            // IMAP servers don't have unique identifier for folders all the time.
            // We'll map them with parent-name relation.

            var currentFolders = await GetFoldersAsync(accountId);

            // These folders don't exist anymore. Remove them.
            var localRemoveFolders = currentFolders.ExceptBy(folders, a => a.RemoteFolderId);

            foreach (var currentFolder in currentFolders)
            {
                // Check if we have this folder locally.
                var remotelyExistFolder = folders.FirstOrDefault(a => a.RemoteFolderId == currentFolder.RemoteFolderId
                && a.ParentRemoteFolderId == currentFolder.ParentRemoteFolderId);

                if (remotelyExistFolder == null)
                {
                    // This folder is removed.
                    // Remove everything for this folder.

                }
            }

            foreach (var folder in folders)
            {
                var currentFolder = await Connection.Table<MailItemFolder>().FirstOrDefaultAsync(a => a.MailAccountId == accountId && a.RemoteFolderId == folder.RemoteFolderId);

                // Nothing is changed, it's still the same folder.
                // Just update Id of the folder.

                if (currentFolder != null)
                    folder.Id = currentFolder.Id;

                await Connection.InsertOrReplaceAsync(folder);
            }
        }

        public async Task<IList<uint>> GetKnownUidsForFolderAsync(Guid folderId)
        {
            var folder = await GetFolderAsync(folderId);

            if (folder == null) return default;

            var mailCopyIds = await GetMailCopyIdsByFolderIdAsync(folderId);

            // Make sure we don't include Ids that doesn't have uid separator.
            // Local drafts might not have it for example.

            return new List<uint>(mailCopyIds.Where(a => a.Contains(MailkitClientExtensions.MailCopyUidSeparator)).Select(a => MailkitClientExtensions.ResolveUid(a)));
        }

        public async Task<MailAccount> UpdateSystemFolderConfigurationAsync(Guid accountId, SystemFolderConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));

            var account = await _accountService.GetAccountAsync(accountId);

            if (account == null)
                throw new ArgumentNullException(nameof(account));

            // Update system folders for this account.

            await Task.WhenAll(UpdateSystemFolderInternalAsync(configuration.SentFolder, SpecialFolderType.Sent),
                               UpdateSystemFolderInternalAsync(configuration.DraftFolder, SpecialFolderType.Draft),
                               UpdateSystemFolderInternalAsync(configuration.JunkFolder, SpecialFolderType.Junk),
                               UpdateSystemFolderInternalAsync(configuration.TrashFolder, SpecialFolderType.Deleted));

            await _accountService.UpdateAccountAsync(account);

            return account;
        }

        private Task UpdateSystemFolderInternalAsync(MailItemFolder folder, SpecialFolderType assignedSpecialFolderType)
        {
            if (folder == null) return Task.CompletedTask;

            folder.IsSticky = true;
            folder.IsSynchronizationEnabled = true;
            folder.IsSystemFolder = true;
            folder.SpecialFolderType = assignedSpecialFolderType;

            return UpdateFolderAsync(folder);
        }

        public async Task ChangeFolderSynchronizationStateAsync(Guid folderId, bool isSynchronizationEnabled)
        {
            var localFolder = await Connection.Table<MailItemFolder>().FirstOrDefaultAsync(a => a.Id == folderId);

            if (localFolder != null)
            {
                localFolder.IsSynchronizationEnabled = isSynchronizationEnabled;

                await UpdateFolderAsync(localFolder).ConfigureAwait(false);
            }
        }

        #region Repository Calls

        public async Task InsertFolderAsync(MailItemFolder folder)
        {
            if (folder == null)
            {
                _logger.Warning("Folder is null. Cannot insert.");

                return;
            }

            var account = await _accountService.GetAccountAsync(folder.MailAccountId);

            if (account == null)
            {
                _logger.Warning("Account with id {MailAccountId} does not exist. Cannot insert folder.", folder.MailAccountId);

                return;
            }

            var existingFolder = await GetFolderAsync(folder.Id).ConfigureAwait(false);

            // IMAP servers don't have unique identifier for folders all the time.
            // So we'll try to match them with remote folder id and account id relation.
            // If we have a match, we'll update the folder instead of inserting.

            existingFolder ??= await GetFolderAsync(folder.MailAccountId, folder.RemoteFolderId).ConfigureAwait(false);

            if (existingFolder == null)
            {
                _logger.Debug("Inserting folder {Id} - {FolderName}", folder.Id, folder.FolderName, folder.MailAccountId);

                await Connection.InsertAsync(folder).ConfigureAwait(false);

                ReportUIChange(new FolderAddedMessage(folder, account));
            }
            else
            {
                _logger.Debug("Folder {Id} - {FolderName} already exists. Updating.", folder.Id, folder.FolderName);

                await UpdateFolderAsync(folder).ConfigureAwait(false);
            }
        }

        private async Task UpdateFolderAsync(MailItemFolder folder)
        {
            if (folder == null)
            {
                _logger.Warning("Folder is null. Cannot update.");

                return;
            }

            var account = await _accountService.GetAccountAsync(folder.MailAccountId).ConfigureAwait(false);
            if (account == null)
            {
                _logger.Warning("Account with id {MailAccountId} does not exist. Cannot update folder.", folder.MailAccountId);
                return;
            }

#if !DEBUG // Annoying
            _logger.Debug("Updating folder {FolderName}", folder.Id, folder.FolderName);
#endif

            await Connection.UpdateAsync(folder).ConfigureAwait(false);

            ReportUIChange(new FolderUpdatedMessage(folder, account));
        }

        private async Task DeleteFolderAsync(MailItemFolder folder)
        {
            if (folder == null)
            {
                _logger.Warning("Folder is null. Cannot delete.");

                return;
            }

            var account = await _accountService.GetAccountAsync(folder.MailAccountId).ConfigureAwait(false);
            if (account == null)
            {
                _logger.Warning("Account with id {MailAccountId} does not exist. Cannot delete folder.", folder.MailAccountId);
                return;
            }

            _logger.Debug("Deleting folder {FolderName}", folder.FolderName);

            await Connection.DeleteAsync(folder).ConfigureAwait(false);

            ReportUIChange(new FolderRemovedMessage(folder, account));
        }

        #endregion

        private Task<List<string>> GetMailCopyIdsByFolderIdAsync(Guid folderId)
        {
            var query = new Query("MailCopy")
                        .Where("FolderId", folderId)
                        .Select("Id");

            return Connection.QueryScalarsAsync<string>(query.GetRawQuery());
        }

        public async Task<List<MailFolderPairMetadata>> GetMailFolderPairMetadatasAsync(IEnumerable<string> mailCopyIds)
        {
            // Get all assignments for all items.
            var query = new Query(nameof(MailCopy))
                        .Join(nameof(MailItemFolder), $"{nameof(MailCopy)}.FolderId", $"{nameof(MailItemFolder)}.Id")
                        .WhereIn($"{nameof(MailCopy)}.Id", mailCopyIds)
                        .SelectRaw($"{nameof(MailCopy)}.Id as MailCopyId, {nameof(MailItemFolder)}.Id as FolderId, {nameof(MailItemFolder)}.RemoteFolderId as RemoteFolderId")
                        .Distinct();

            var rowQuery = query.GetRawQuery();

            return await Connection.QueryAsync<MailFolderPairMetadata>(rowQuery);
        }

        public Task<List<MailFolderPairMetadata>> GetMailFolderPairMetadatasAsync(string mailCopyId)
            => GetMailFolderPairMetadatasAsync(new List<string>() { mailCopyId });

        public async Task SetSpecialFolderAsync(Guid folderId, SpecialFolderType type)
            => await Connection.ExecuteAsync("UPDATE MailItemFolder SET SpecialFolderType = ? WHERE Id = ?", type, folderId);

        public async Task<List<MailItemFolder>> GetSynchronizationFoldersAsync(SynchronizationOptions options)
        {
            var folders = new List<MailItemFolder>();

            if (options.Type == SynchronizationType.Inbox)
            {
                var inboxFolder = await GetSpecialFolderByAccountIdAsync(options.AccountId, SpecialFolderType.Inbox);
                var sentFolder = await GetSpecialFolderByAccountIdAsync(options.AccountId, SpecialFolderType.Sent);
                var draftFolder = await GetSpecialFolderByAccountIdAsync(options.AccountId, SpecialFolderType.Draft);

                // For properly creating threads we need Sent and Draft to be synchronized as well.

                if (sentFolder != null && sentFolder.IsSynchronizationEnabled)
                {
                    folders.Add(sentFolder);
                }

                if (draftFolder != null && draftFolder.IsSynchronizationEnabled)
                {
                    folders.Add(draftFolder);
                }

                // User might've disabled inbox synchronization somehow...
                if (inboxFolder != null && inboxFolder.IsSynchronizationEnabled)
                {
                    folders.Add(inboxFolder);
                }
            }
            else if (options.Type == SynchronizationType.Full)
            {
                // Only get sync enabled folders.

                var synchronizationFolders = await Connection.Table<MailItemFolder>()
                    .Where(a => a.MailAccountId == options.AccountId && a.IsSynchronizationEnabled)
                    .OrderBy(a => a.SpecialFolderType)
                    .ToListAsync();

                folders.AddRange(synchronizationFolders);
            }
            else if (options.Type == SynchronizationType.Custom)
            {
                // Only get the specified and enabled folders.

                var synchronizationFolders = await Connection.Table<MailItemFolder>()
                    .Where(a => a.MailAccountId == options.AccountId && a.IsSynchronizationEnabled && options.SynchronizationFolderIds.Contains(a.Id))
                    .ToListAsync();

                folders.AddRange(synchronizationFolders);
            }

            return folders;
        }

        public Task<MailItemFolder> GetFolderAsync(Guid accountId, string remoteFolderId)
            => Connection.Table<MailItemFolder>().FirstOrDefaultAsync(a => a.MailAccountId == accountId && a.RemoteFolderId == remoteFolderId);

        // v2
        public async Task BulkUpdateFolderStructureAsync(Guid accountId, List<MailItemFolder> allFolders)
        {
            var existingFolders = await GetFoldersAsync(accountId).ConfigureAwait(false);

            var foldersToInsert = allFolders.ExceptBy(existingFolders, a => a.RemoteFolderId);
            var foldersToDelete = existingFolders.ExceptBy(allFolders, a => a.RemoteFolderId);
            var foldersToUpdate = allFolders.Except(foldersToInsert).Except(foldersToDelete);

            _logger.Debug("Found {0} folders to insert, {1} folders to update and {2} folders to delete.",
                          foldersToInsert.Count(),
                          foldersToUpdate.Count(),
                          foldersToDelete.Count());

            foreach (var folder in foldersToInsert)
            {
                await InsertFolderAsync(folder).ConfigureAwait(false);
            }

            foreach (var folder in foldersToUpdate)
            {
                await UpdateFolderAsync(folder).ConfigureAwait(false);
            }

            foreach (var folder in foldersToDelete)
            {
                await DeleteFolderAsync(folder).ConfigureAwait(false);
            }
        }



        public async Task DeleteFolderAsync(Guid accountId, string remoteFolderId)
        {
            var folder = await GetFolderAsync(accountId, remoteFolderId);

            if (folder == null)
            {
                _logger.Warning("Folder with id {RemoteFolderId} does not exist. Delete folder canceled.", remoteFolderId);

                return;
            }

            await DeleteFolderAsync(folder).ConfigureAwait(false);
        }

        public async Task ChangeFolderShowUnreadCountStateAsync(Guid folderId, bool showUnreadCount)
        {
            var localFolder = await GetFolderAsync(folderId);

            if (localFolder != null)
            {
                localFolder.ShowUnreadCount = showUnreadCount;

                await UpdateFolderAsync(localFolder).ConfigureAwait(false);
            }
        }

        // Inbox folder is always included for account menu item unread count.
        public Task<List<MailItemFolder>> GetUnreadUpdateFoldersAsync(Guid accountId)
            => Connection.Table<MailItemFolder>().Where(a => a.MailAccountId == accountId && (a.ShowUnreadCount || a.SpecialFolderType == SpecialFolderType.Inbox)).ToListAsync();

        public async Task TestAsync()
        {
            var account = new MailAccount()
            {
                Address = "test@test.com",
                ProviderType = MailProviderType.Gmail,
                Name = "Test Account",
                Id = Guid.NewGuid()
            };

            await Connection.InsertAsync(account);

            var pref = new MailAccountPreferences
            {
                Id = Guid.NewGuid(),
                AccountId = account.Id
            };

            await Connection.InsertAsync(pref);

            ReportUIChange(new AccountCreatedMessage(account));
        }

        public async Task<bool> IsInboxAvailableForAccountAsync(Guid accountId)
            => (await Connection.Table<MailItemFolder>()
            .Where(a => a.SpecialFolderType == SpecialFolderType.Inbox && a.MailAccountId == accountId)
            .CountAsync()) == 1;

        public Task UpdateFolderLastSyncDateAsync(Guid folderId)
            => Connection.ExecuteAsync("UPDATE MailItemFolder SET LastSynchronizedDate = ? WHERE Id = ?", DateTime.UtcNow, folderId);
    }
}
