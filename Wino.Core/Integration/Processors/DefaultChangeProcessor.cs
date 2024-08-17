using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Services;

namespace Wino.Core.Integration.Processors
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
        Task UpdateAccountAsync(MailAccount account);
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
        Task UpdateRemoteAliasInformationAsync(MailAccount account, List<RemoteAccountAlias> remoteAccountAliases);
    }

    public interface IGmailChangeProcessor : IDefaultChangeProcessor
    {
        Task MapLocalDraftAsync(string mailCopyId, string newDraftId, string newThreadId);
    }

    public interface IOutlookChangeProcessor : IDefaultChangeProcessor
    {
        /// <summary>
        /// Interrupted initial synchronization may cause downloaded mails to be saved in the database twice.
        /// Since downloading mime is costly in Outlook, we need to check if the actual copy of the message has been saved before.
        /// </summary>
        /// <param name="messageId">MailCopyId of the message.</param>
        /// <returns>Whether the mime has b</returns>
        Task<bool> IsMailExistsAsync(string messageId);

        /// <summary>
        /// Checks whether the mail exists in the folder.
        /// When deciding Create or Update existing mail, we need to check if the mail exists in the folder.
        /// </summary>
        /// <param name="messageId">Message id</param>
        /// <param name="folderId">Folder's local id.</param>
        /// <returns>Whether mail exists in the folder or not.</returns>
        Task<bool> IsMailExistsInFolderAsync(string messageId, Guid folderId);

        /// <summary>
        /// Updates Folder's delta synchronization identifier.
        /// Only used in Outlook since it does per-folder sync.
        /// </summary>
        /// <param name="folderId">Folder id</param>
        /// <param name="synchronizationIdentifier">New synchronization identifier.</param>
        /// <returns>New identifier if success.</returns>
        Task UpdateFolderDeltaSynchronizationIdentifierAsync(Guid folderId, string deltaSynchronizationIdentifier);

        /// <summary>
        /// Outlook may expire folder's delta token after a while.
        /// Recommended action for this scenario is to reset token and do full sync.
        /// This method resets the token for the given folder.
        /// </summary>
        /// <param name="folderId">Local folder id to reset token for.</param>
        /// <returns>Empty string to assign folder delta sync for.</returns>
        Task<string> ResetFolderDeltaTokenAsync(Guid folderId);

        /// <summary>
        /// Outlook may expire account's delta token after a while.
        /// This will result returning 410 GONE response from the API for synchronizing folders.
        /// This method resets the token for the given account for re-syncing folders.
        /// </summary>
        /// <param name="accountId">Account identifier to reset delta token for.</param>
        /// <returns>Empty string to assign account delta sync for.</returns>
        Task<string> ResetAccountDeltaTokenAsync(Guid accountId);
    }

    public interface IImapChangeProcessor : IDefaultChangeProcessor
    {
        /// <summary>
        /// Returns all known uids for the given folder.
        /// </summary>
        /// <param name="folderId">Folder id to retrieve uIds for.</param>
        Task<IList<uint>> GetKnownUidsForFolderAsync(Guid folderId);
    }

    public class DefaultChangeProcessor(IDatabaseService databaseService,
                                  IFolderService folderService,
                                  IMailService mailService,
                                  IAccountService accountService,
                                  IMimeFileService mimeFileService) : BaseDatabaseService(databaseService), IDefaultChangeProcessor
    {
        protected IMailService MailService = mailService;

        protected IFolderService FolderService = folderService;
        protected IAccountService AccountService = accountService;
        private readonly IMimeFileService _mimeFileService = mimeFileService;

        public Task<string> UpdateAccountDeltaSynchronizationIdentifierAsync(Guid accountId, string synchronizationDeltaIdentifier)
            => AccountService.UpdateSynchronizationIdentifierAsync(accountId, synchronizationDeltaIdentifier);

        public Task ChangeFlagStatusAsync(string mailCopyId, bool isFlagged)
            => MailService.ChangeFlagStatusAsync(mailCopyId, isFlagged);

        public Task ChangeMailReadStatusAsync(string mailCopyId, bool isRead)
            => MailService.ChangeReadStatusAsync(mailCopyId, isRead);

        public Task DeleteAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId)
            => MailService.DeleteAssignmentAsync(accountId, mailCopyId, remoteFolderId);

        public Task CreateAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId)
            => MailService.CreateAssignmentAsync(accountId, mailCopyId, remoteFolderId);

        public Task DeleteMailAsync(Guid accountId, string mailId)
            => MailService.DeleteMailAsync(accountId, mailId);

        public Task<bool> CreateMailAsync(Guid accountId, NewMailItemPackage package)
            => MailService.CreateMailAsync(accountId, package);

        public Task<List<MailItemFolder>> GetExistingFoldersAsync(Guid accountId)
            => FolderService.GetFoldersAsync(accountId);

        public Task<bool> MapLocalDraftAsync(Guid accountId, Guid localDraftCopyUniqueId, string newMailCopyId, string newDraftId, string newThreadId)
            => MailService.MapLocalDraftAsync(accountId, localDraftCopyUniqueId, newMailCopyId, newDraftId, newThreadId);

        public Task<List<MailItemFolder>> GetLocalFoldersAsync(Guid accountId)
            => FolderService.GetFoldersAsync(accountId);

        public Task<List<MailItemFolder>> GetSynchronizationFoldersAsync(SynchronizationOptions options)
            => FolderService.GetSynchronizationFoldersAsync(options);

        public Task DeleteFolderAsync(Guid accountId, string remoteFolderId)
            => FolderService.DeleteFolderAsync(accountId, remoteFolderId);

        public Task InsertFolderAsync(MailItemFolder folder)
            => FolderService.InsertFolderAsync(folder);

        public Task UpdateFolderAsync(MailItemFolder folder)
            => FolderService.UpdateFolderAsync(folder);

        public Task<List<MailCopy>> GetDownloadedUnreadMailsAsync(Guid accountId, IEnumerable<string> downloadedMailCopyIds)
            => MailService.GetDownloadedUnreadMailsAsync(accountId, downloadedMailCopyIds);



        public Task SaveMimeFileAsync(Guid fileId, MimeMessage mimeMessage, Guid accountId)
            => _mimeFileService.SaveMimeMessageAsync(fileId, mimeMessage, accountId);

        public Task UpdateFolderLastSyncDateAsync(Guid folderId)
            => FolderService.UpdateFolderLastSyncDateAsync(folderId);

        public Task UpdateAccountAsync(MailAccount account)
            => AccountService.UpdateAccountAsync(account);

        public Task UpdateRemoteAliasInformationAsync(MailAccount account, List<RemoteAccountAlias> remoteAccountAliases)
            => AccountService.UpdateRemoteAliasInformationAsync(account, remoteAccountAliases);
    }
}
