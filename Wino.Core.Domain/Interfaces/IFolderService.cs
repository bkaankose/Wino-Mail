using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.Interfaces
{
    public interface IFolderService
    {
        Task<AccountFolderTree> GetFolderStructureForAccountAsync(Guid accountId, bool includeHiddenFolders);
        Task<MailItemFolder> GetFolderAsync(Guid folderId);
        Task<MailItemFolder> GetFolderAsync(Guid accountId, string remoteFolderId);
        Task<List<MailItemFolder>> GetFoldersAsync(Guid accountId);
        Task<List<MailItemFolder>> GetUnreadUpdateFoldersAsync(Guid accountId);
        Task SetSpecialFolderAsync(Guid folderId, SpecialFolderType type);
        Task<MailItemFolder> GetSpecialFolderByAccountIdAsync(Guid accountId, SpecialFolderType type);
        Task<int> GetCurrentItemCountForFolder(Guid folderId);
        Task<int> GetFolderNotificationBadgeAsync(Guid folderId);
        Task ChangeStickyStatusAsync(Guid folderId, bool isSticky);
        Task UpdateCustomServerMailListAsync(Guid accountId, List<MailItemFolder> folders);

        Task<MailAccount> UpdateSystemFolderConfigurationAsync(Guid accountId, SystemFolderConfiguration configuration);
        Task ChangeFolderSynchronizationStateAsync(Guid folderId, bool isSynchronizationEnabled);
        Task ChangeFolderShowUnreadCountStateAsync(Guid folderId, bool showUnreadCount);

        Task<List<MailItemFolder>> GetSynchronizationFoldersAsync(SynchronizationOptions options);

        /// <summary>
        /// Returns the folder - mail mapping for the given mail copy ids.
        /// </summary>
        Task<List<MailFolderPairMetadata>> GetMailFolderPairMetadatasAsync(IEnumerable<string> mailCopyIds);

        /// <summary>
        /// Returns the folder - mail mapping for the given mail copy id.
        /// </summary>
        Task<List<MailFolderPairMetadata>> GetMailFolderPairMetadatasAsync(string mailCopyId);

        // v2

        /// <summary>
        /// Performs bulk update for the given folders.
        /// Used in Gmail.
        /// </summary>
        /// <param name="accountId">Account that folders belong to.</param>
        /// <param name="allFolders">Folders to update.</param>
        Task BulkUpdateFolderStructureAsync(Guid accountId, List<MailItemFolder> allFolders);

        /// <summary>
        /// Deletes the folder for the given account by remote folder id.
        /// </summary>
        /// <param name="accountId">Account to remove from.</param>
        /// <param name="remoteFolderId">Remote folder id.</param>
        /// <returns></returns>
        Task DeleteFolderAsync(Guid accountId, string remoteFolderId);

        /// <summary>
        /// Adds a new folder.
        /// </summary>
        /// <param name="folder">Folder to add.</param>
        Task InsertFolderAsync(MailItemFolder folder);


        /// <summary>
        /// Returns the known uids for the given folder.
        /// Only used for IMAP
        /// </summary>
        /// <param name="folderId">Folder to get uIds for</param>
        Task<IList<uint>> GetKnownUidsForFolderAsync(Guid folderId);

        /// <summary>
        /// Checks if Inbox special folder exists for an account.
        /// </summary>
        /// <param name="accountId">Account id to check for.</param>
        /// <returns>True if Inbox exists, False if not.</returns>
        Task<bool> IsInboxAvailableForAccountAsync(Guid accountId);

        /// <summary>
        /// Updates folder's LastSynchronizedDate to now.
        /// </summary>
        /// <param name="folderId">Folder to update.</param>
        Task UpdateFolderLastSyncDateAsync(Guid folderId);

        Task TestAsync();

        /// <summary>
        /// Updates the given folder.
        /// </summary>
        /// <param name="folder">Folder to update.</param>
        Task UpdateFolderAsync(MailItemFolder folder);
    }
}
