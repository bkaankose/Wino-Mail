using Wino.Domain.Entities;
using Wino.Domain.Enums;
using Wino.Domain.Models.Accounts;
using Wino.Domain.Models.Folders;
using Wino.Domain.Models.MailItem;
using Wino.Domain.Models.Synchronization;

namespace Wino.Domain.Interfaces
{
    public interface IFolderService
    {
        Task<AccountFolderTree> GetFolderStructureForAccountAsync(Guid accountId, bool includeHiddenFolders);
        Task<MailItemFolder> GetFolderAsync(Guid folderId);
        Task<MailItemFolder> GetFolderAsync(Guid accountId, string remoteFolderId);
        Task<List<MailItemFolder>> GetFoldersAsync(Guid accountId);
        Task<MailItemFolder> GetSpecialFolderByAccountIdAsync(Guid accountId, SpecialFolderType type);
        Task<int> GetCurrentItemCountForFolder(Guid folderId);
        Task<int> GetFolderNotificationBadgeAsync(Guid folderId);
        Task ChangeStickyStatusAsync(Guid folderId, bool isSticky);

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

        /// <summary>
        /// Updates the given folder.
        /// </summary>
        /// <param name="folder">Folder to update.</param>
        Task UpdateFolderAsync(MailItemFolder folder);

        /// <summary>
        /// Returns the active folder menu items for the given account for UI.
        /// </summary>
        /// <param name="accountMenuItem">Account to get folder menu items for.</param>
        // Task<IEnumerable<IMenuItem>> GetAccountFoldersForDisplayAsync(IAccountMenuItem accountMenuItem);

        /// <summary>
        /// Returns a list of unread item counts for the given account ids.
        /// Every folder that is marked as show unread badge is included.
        /// </summary>
        /// <param name="accountIds">Account ids to get unread folder counts for.</param>
        Task<List<UnreadItemCountResult>> GetUnreadItemCountResultsAsync(IEnumerable<Guid> accountIds);
        Task<List<MailItemFolder>> GetVisibleFoldersAsync(Guid accountId);

        /// <summary>
        /// Returns the child folders of the given folder.
        /// </summary>
        /// <param name="accountId">Account id to get folders for.</param>
        /// <param name="parentRemoteFolderId">Parent folder id to get children for.</param>
        Task<List<MailItemFolder>> GetChildFoldersAsync(Guid accountId, string parentRemoteFolderId);
    }
}
