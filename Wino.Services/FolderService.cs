using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SqlKata;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Messaging.UI;
using Wino.Services.Extensions;

namespace Wino.Services
{
    public class FolderService : BaseDatabaseService, IFolderService
    {
        private readonly IAccountService _accountService;
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
                               IAccountService accountService) : base(databaseService)
        {
            _accountService = accountService;
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
                            bool belongsToExistingParent = await Connection
                                .Table<MailItemFolder>()
                                .Where(a => unstickyItem.ParentRemoteFolderId == a.RemoteFolderId)
                                .CountAsync() > 0;

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


        public Task<IEnumerable<IMenuItem>> GetAccountFoldersForDisplayAsync(IAccountMenuItem accountMenuItem)
        {
            if (accountMenuItem is IMergedAccountMenuItem mergedAccountFolderMenuItem)
            {
                return GetMergedAccountFolderMenuItemsAsync(mergedAccountFolderMenuItem);
            }
            else
            {
                return GetSingleAccountFolderMenuItemsAsync(accountMenuItem);
            }
        }

        private async Task<FolderMenuItem> GetPreparedFolderMenuItemRecursiveAsync(MailAccount account, MailItemFolder parentFolder, IMenuItem parentMenuItem)
        {
            // Localize category folder name.
            if (parentFolder.SpecialFolderType == SpecialFolderType.Category) parentFolder.FolderName = Translator.CategoriesFolderNameOverride;

            var query = new Query(nameof(MailItemFolder))
                        .Where(nameof(MailItemFolder.ParentRemoteFolderId), parentFolder.RemoteFolderId)
                        .Where(nameof(MailItemFolder.MailAccountId), parentFolder.MailAccountId);

            var preparedFolder = new FolderMenuItem(parentFolder, account, parentMenuItem);

            var childFolders = await Connection.QueryAsync<MailItemFolder>(query.GetRawQuery()).ConfigureAwait(false);

            if (childFolders.Any())
            {
                foreach (var subChildFolder in childFolders)
                {
                    var preparedChild = await GetPreparedFolderMenuItemRecursiveAsync(account, subChildFolder, preparedFolder);

                    if (preparedChild == null) continue;

                    preparedFolder.SubMenuItems.Add(preparedChild);
                }
            }

            return preparedFolder;
        }

        private async Task<IEnumerable<IMenuItem>> GetSingleAccountFolderMenuItemsAsync(IAccountMenuItem accountMenuItem)
        {
            var accountId = accountMenuItem.EntityId.Value;
            var preparedFolderMenuItems = new List<IMenuItem>();

            // Get all folders for the account. Excluding hidden folders.
            var folders = await GetVisibleFoldersAsync(accountId).ConfigureAwait(false);

            if (!folders.Any()) return new List<IMenuItem>();

            var mailAccount = accountMenuItem.HoldingAccounts.First();

            var listingFolders = folders.OrderBy(a => a.SpecialFolderType);

            var moreFolder = MailItemFolder.CreateMoreFolder();
            var categoryFolder = MailItemFolder.CreateCategoriesFolder();

            var moreFolderMenuItem = new FolderMenuItem(moreFolder, mailAccount, accountMenuItem);
            var categoryFolderMenuItem = new FolderMenuItem(categoryFolder, mailAccount, accountMenuItem);

            foreach (var item in listingFolders)
            {
                // Category type folders should be skipped. They will be categorized under virtual category folder.
                if (ServiceConstants.SubCategoryFolderLabelIds.Contains(item.RemoteFolderId)) continue;

                bool skipEmptyParentRemoteFolders = mailAccount.ProviderType == MailProviderType.Gmail;

                if (skipEmptyParentRemoteFolders && !string.IsNullOrEmpty(item.ParentRemoteFolderId)) continue;

                // Sticky items belong to account menu item directly. Rest goes to More folder.
                IMenuItem parentFolderMenuItem = item.IsSticky ? accountMenuItem : ServiceConstants.SubCategoryFolderLabelIds.Contains(item.FolderName.ToUpper()) ? categoryFolderMenuItem : moreFolderMenuItem;

                var preparedItem = await GetPreparedFolderMenuItemRecursiveAsync(mailAccount, item, parentFolderMenuItem).ConfigureAwait(false);

                // Don't add menu items that are prepared for More folder. They've been included in More virtual folder already.
                // We'll add More folder later on at the end of the list.

                if (preparedItem == null) continue;

                if (item.IsSticky)
                {
                    preparedFolderMenuItems.Add(preparedItem);
                }
                else if (parentFolderMenuItem is FolderMenuItem baseParentFolderMenuItem)
                {
                    baseParentFolderMenuItem.SubMenuItems.Add(preparedItem);
                }
            }

            // Only add category folder if it's Gmail.
            if (mailAccount.ProviderType == MailProviderType.Gmail) preparedFolderMenuItems.Add(categoryFolderMenuItem);

            // Only add More folder if there are any items in it.
            if (moreFolderMenuItem.SubMenuItems.Any()) preparedFolderMenuItems.Add(moreFolderMenuItem);

            return preparedFolderMenuItems;
        }

        private async Task<IEnumerable<IMenuItem>> GetMergedAccountFolderMenuItemsAsync(IMergedAccountMenuItem mergedAccountFolderMenuItem)
        {
            var holdingAccounts = mergedAccountFolderMenuItem.HoldingAccounts;

            if (holdingAccounts == null || !holdingAccounts.Any()) return [];

            var preparedFolderMenuItems = new List<IMenuItem>();

            // First gather all account folders.
            // Prepare single menu items for both of them.

            var allAccountFolders = new List<List<MailItemFolder>>();

            foreach (var account in holdingAccounts)
            {
                var accountFolders = await GetVisibleFoldersAsync(account.Id).ConfigureAwait(false);

                allAccountFolders.Add(accountFolders);
            }

            var commonFolders = FindCommonFolders(allAccountFolders);

            // Prepare menu items for common folders.
            foreach (var commonFolderType in commonFolders)
            {
                var folderItems = allAccountFolders.SelectMany(a => a.Where(b => b.SpecialFolderType == commonFolderType)).Cast<IMailItemFolder>().ToList();
                var menuItem = new MergedAccountFolderMenuItem(folderItems, null, mergedAccountFolderMenuItem.Parameter);

                preparedFolderMenuItems.Add(menuItem);
            }

            return preparedFolderMenuItems;
        }

        private HashSet<SpecialFolderType> FindCommonFolders(List<List<MailItemFolder>> lists)
        {
            var allSpecialTypesExceptOther = Enum.GetValues(typeof(SpecialFolderType)).Cast<SpecialFolderType>().Where(a => a != SpecialFolderType.Other).ToList();

            // Start with all special folder types from the first list
            var commonSpecialFolderTypes = new HashSet<SpecialFolderType>(allSpecialTypesExceptOther);

            // Intersect with special folder types from all lists
            foreach (var list in lists)
            {
                commonSpecialFolderTypes.IntersectWith(list.Select(f => f.SpecialFolderType));
            }

            return commonSpecialFolderTypes;
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
        {
            var query = new Query(nameof(MailItemFolder))
                        .Where(nameof(MailItemFolder.MailAccountId), accountId)
                        .OrderBy(nameof(MailItemFolder.SpecialFolderType));

            return Connection.QueryAsync<MailItemFolder>(query.GetRawQuery());
        }

        public Task<List<MailItemFolder>> GetVisibleFoldersAsync(Guid accountId)
        {
            var query = new Query(nameof(MailItemFolder))
                        .Where(nameof(MailItemFolder.MailAccountId), accountId)
                        .Where(nameof(MailItemFolder.IsHidden), false)
                        .OrderBy(nameof(MailItemFolder.SpecialFolderType));

            return Connection.QueryAsync<MailItemFolder>(query.GetRawQuery());
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

            // Update system folders for this account.

            await Task.WhenAll(UpdateSystemFolderInternalAsync(configuration.SentFolder, SpecialFolderType.Sent),
                               UpdateSystemFolderInternalAsync(configuration.DraftFolder, SpecialFolderType.Draft),
                               UpdateSystemFolderInternalAsync(configuration.JunkFolder, SpecialFolderType.Junk),
                               UpdateSystemFolderInternalAsync(configuration.TrashFolder, SpecialFolderType.Deleted),
                               UpdateSystemFolderInternalAsync(configuration.ArchiveFolder, SpecialFolderType.Archive));


            return await _accountService.GetAccountAsync(accountId).ConfigureAwait(false);
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

                Messenger.Send(new FolderSynchronizationEnabled(localFolder));
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
            }
            else
            {
                // TODO: This is not alright. We should've updated the folder instead of inserting.
                // Now we need to match the properties that user might've set locally.

                folder.Id = existingFolder.Id;
                folder.IsSticky = existingFolder.IsSticky;
                folder.SpecialFolderType = existingFolder.SpecialFolderType;
                folder.ShowUnreadCount = existingFolder.ShowUnreadCount;
                folder.TextColorHex = existingFolder.TextColorHex;
                folder.BackgroundColorHex = existingFolder.BackgroundColorHex;

                _logger.Debug("Folder {Id} - {FolderName} already exists. Updating.", folder.Id, folder.FolderName);

                await UpdateFolderAsync(folder).ConfigureAwait(false);
            }
        }

        public async Task UpdateFolderAsync(MailItemFolder folder)
        {
            if (folder == null)
            {
                _logger.Warning("Folder is null. Cannot update.");

                return;
            }

            _logger.Debug("Updating folder {FolderName}", folder.Id, folder.FolderName);

            await Connection.UpdateAsync(folder).ConfigureAwait(false);
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

            // Delete all existing mails from this folder.
            await Connection.ExecuteAsync("DELETE FROM MailCopy WHERE FolderId = ?", folder.Id);

            // TODO: Delete MIME messages from the disk.
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

        public async Task<List<MailItemFolder>> GetSynchronizationFoldersAsync(SynchronizationOptions options)
        {
            var folders = new List<MailItemFolder>();

            if (options.Type == SynchronizationType.FullFolders)
            {
                // Only get sync enabled folders.

                var synchronizationFolders = await Connection.Table<MailItemFolder>()
                    .Where(a => a.MailAccountId == options.AccountId && a.IsSynchronizationEnabled)
                    .OrderBy(a => a.SpecialFolderType)
                    .ToListAsync();

                folders.AddRange(synchronizationFolders);
            }
            else
            {
                // Inbox, Sent and Draft folders must always be synchronized regardless of whether they are enabled or not.
                // Custom folder sync will add additional folders to the list if not specified.

                var mustHaveFolders = await GetInboxSynchronizationFoldersAsync(options.AccountId);

                if (options.Type == SynchronizationType.InboxOnly)
                {
                    return mustHaveFolders;
                }
                else if (options.Type == SynchronizationType.CustomFolders)
                {
                    // Only get the specified and enabled folders.

                    var synchronizationFolders = await Connection.Table<MailItemFolder>()
                        .Where(a => a.MailAccountId == options.AccountId && options.SynchronizationFolderIds.Contains(a.Id))
                        .ToListAsync();

                    // Order is important for moving.
                    // By implementation, removing mail folders must be synchronized first. Requests are made in that order for custom sync.
                    // eg. Moving item from Folder A to Folder B. If we start syncing Folder B first, we might miss adding assignment for Folder A.

                    var orderedCustomFolders = synchronizationFolders.OrderBy(a => options.SynchronizationFolderIds.IndexOf(a.Id));

                    foreach (var item in orderedCustomFolders)
                    {
                        if (!mustHaveFolders.Any(a => a.Id == item.Id))
                        {
                            mustHaveFolders.Add(item);
                        }
                    }
                }

                return mustHaveFolders;
            }

            return folders;
        }

        private async Task<List<MailItemFolder>> GetInboxSynchronizationFoldersAsync(Guid accountId)
        {
            var folders = new List<MailItemFolder>();

            var inboxFolder = await GetSpecialFolderByAccountIdAsync(accountId, SpecialFolderType.Inbox);
            var sentFolder = await GetSpecialFolderByAccountIdAsync(accountId, SpecialFolderType.Sent);
            var draftFolder = await GetSpecialFolderByAccountIdAsync(accountId, SpecialFolderType.Draft);
            var deletedFolder = await GetSpecialFolderByAccountIdAsync(accountId, SpecialFolderType.Deleted);

            if (deletedFolder != null)
            {
                folders.Add(deletedFolder);
            }

            if (inboxFolder != null)
            {
                folders.Add(inboxFolder);
            }

            // For properly creating threads we need Sent and Draft to be synchronized as well.

            if (sentFolder != null)
            {
                folders.Add(sentFolder);
            }

            if (draftFolder != null)
            {
                folders.Add(draftFolder);
            }

            return folders;
        }

        public Task<MailItemFolder> GetFolderAsync(Guid accountId, string remoteFolderId)
            => Connection.Table<MailItemFolder>().FirstOrDefaultAsync(a => a.MailAccountId == accountId && a.RemoteFolderId == remoteFolderId);

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

        public async Task<bool> IsInboxAvailableForAccountAsync(Guid accountId)
            => await Connection.Table<MailItemFolder>()
            .Where(a => a.SpecialFolderType == SpecialFolderType.Inbox && a.MailAccountId == accountId)
            .CountAsync() == 1;

        public Task UpdateFolderLastSyncDateAsync(Guid folderId)
            => Connection.ExecuteAsync("UPDATE MailItemFolder SET LastSynchronizedDate = ? WHERE Id = ?", DateTime.UtcNow, folderId);

        public Task<List<UnreadItemCountResult>> GetUnreadItemCountResultsAsync(IEnumerable<Guid> accountIds)
        {
            var query = new Query(nameof(MailCopy))
                        .Join(nameof(MailItemFolder), $"{nameof(MailCopy)}.FolderId", $"{nameof(MailItemFolder)}.Id")
                        .WhereIn($"{nameof(MailItemFolder)}.MailAccountId", accountIds)
                        .Where($"{nameof(MailCopy)}.IsRead", 0)
                        .Where($"{nameof(MailItemFolder)}.ShowUnreadCount", 1)
                        .SelectRaw($"{nameof(MailItemFolder)}.Id as FolderId, {nameof(MailItemFolder)}.SpecialFolderType as SpecialFolderType, count (DISTINCT {nameof(MailCopy)}.Id) as UnreadItemCount, {nameof(MailItemFolder)}.MailAccountId as AccountId")
                        .GroupBy($"{nameof(MailItemFolder)}.Id");

            return Connection.QueryAsync<UnreadItemCountResult>(query.GetRawQuery());
        }
    }
}
