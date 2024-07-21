using MimeKit;
using Wino.Domain.Entities;
using Wino.Domain.Models.MailItem;
using Wino.Domain.Models.Synchronization;

namespace Wino.Domain.Interfaces
{
    /// <summary>
    /// Database change processor that handles common operations for all synchronizers.
    /// When a synchronizer detects a change, it should call the appropriate method in this class to reflect the change in the database.
    /// Different synchronizers might need additional implementations.
    /// <see cref="IGmailChangeProcessor"/>,  <see cref="IOutlookChangeProcessor"/> and  <see cref="IImapChangeProcessor"/>
    /// None of the synchronizers can directly change anything in the database.
    /// </summary>
    public interface IDefaultChangeProcessor
    {
        Task<string> UpdateAccountDeltaSynchronizationIdentifierAsync(Guid accountId, string deltaSynchronizationIdentifier);
        Task CreateAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId);
        Task DeleteAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId);
        Task ChangeMailReadStatusAsync(string mailCopyId, bool isRead);
        Task ChangeFlagStatusAsync(string mailCopyId, bool isFlagged);
        Task<bool> CreateMailAsync(Guid AccountId, NewMailItemPackage package);
        Task DeleteMailAsync(Guid accountId, string mailId);
        Task<List<MailCopy>> GetDownloadedUnreadMailsAsync(Guid accountId, IEnumerable<string> downloadedMailCopyIds);
        Task SaveMimeFileAsync(Guid fileId, MimeMessage mimeMessage, Guid accountId);
        Task DeleteFolderAsync(Guid accountId, string remoteFolderId);
        Task InsertFolderAsync(MailItemFolder folder);
        Task UpdateFolderAsync(MailItemFolder folder);

        /// <summary>
        /// Returns the list of folders that are available for account.
        /// </summary>
        /// <param name="accountId">Account id to get folders for.</param>
        /// <returns>All folders.</returns>
        Task<List<MailItemFolder>> GetLocalFoldersAsync(Guid accountId);

        Task<List<MailItemFolder>> GetSynchronizationFoldersAsync(SynchronizationOptions options);

        Task<bool> MapLocalDraftAsync(Guid accountId, Guid localDraftCopyUniqueId, string newMailCopyId, string newDraftId, string newThreadId);
        Task UpdateFolderLastSyncDateAsync(Guid folderId);

        Task<List<MailItemFolder>> GetExistingFoldersAsync(Guid accountId);
    }
}
