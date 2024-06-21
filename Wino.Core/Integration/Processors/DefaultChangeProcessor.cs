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
        Task<string> UpdateAccountDeltaSynchronizationIdentifierAsync(Guid accountId, string deltaSynchronizationIdentifier);
        Task CreateAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId);
        Task DeleteAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId);
        Task ChangeMailReadStatusAsync(string mailCopyId, bool isRead);
        Task ChangeFlagStatusAsync(string mailCopyId, bool isFlagged);
        Task<bool> CreateMailAsync(Guid AccountId, NewMailItemPackage package);
        Task DeleteMailAsync(Guid accountId, string mailId);
        Task<List<MailCopy>> GetDownloadedUnreadMailsAsync(Guid accountId, IEnumerable<string> downloadedMailCopyIds);
        Task SaveMimeFileAsync(Guid fileId, MimeMessage mimeMessage, Guid accountId);
        Task UpdateFolderStructureAsync(Guid accountId, List<MailItemFolder> allFolders);
        Task DeleteFolderAsync(Guid accountId, string remoteFolderId);
        Task<List<MailItemFolder>> GetSynchronizationFoldersAsync(SynchronizationOptions options);
        Task InsertFolderAsync(MailItemFolder folder);
        Task<bool> MapLocalDraftAsync(Guid accountId, Guid localDraftCopyUniqueId, string newMailCopyId, string newDraftId, string newThreadId);
        Task UpdateFolderLastSyncDateAsync(Guid folderId);
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
        /// Updates Folder's delta synchronization identifier.
        /// Only used in Outlook since it does per-folder sync.
        /// </summary>
        /// <param name="folderId">Folder id</param>
        /// <param name="synchronizationIdentifier">New synchronization identifier.</param>
        /// <returns>New identifier if success.</returns>
        Task UpdateFolderDeltaSynchronizationIdentifierAsync(Guid folderId, string deltaSynchronizationIdentifier);
    }

    public interface IImapChangeProcessor : IDefaultChangeProcessor
    {
        /// <summary>
        /// Returns all known uids for the given folder.
        /// </summary>
        /// <param name="folderId">Folder id to retrieve uIds for.</param>
        Task<IList<uint>> GetKnownUidsForFolderAsync(Guid folderId);

        /// <summary>
        /// Returns the list of folders that are available for account.
        /// </summary>
        /// <param name="accountId">Account id to get folders for.</param>
        /// <returns>All folders.</returns>
        Task<List<MailItemFolder>> GetLocalIMAPFoldersAsync(Guid accountId);

        /// <summary>
        /// Updates folder.
        /// </summary>
        /// <param name="folder">Folder to update.</param>
        Task UpdateFolderAsync(MailItemFolder folder);
    }

    public class DefaultChangeProcessor(IDatabaseService databaseService,
                                  IFolderService folderService,
                                  IMailService mailService,
                                  IAccountService accountService,
                                  IMimeFileService mimeFileService) : BaseDatabaseService(databaseService), IDefaultChangeProcessor
    {
        protected IMailService MailService = mailService;

        protected IFolderService FolderService = folderService;
        private readonly IAccountService _accountService = accountService;
        private readonly IMimeFileService _mimeFileService = mimeFileService;

        public Task<string> UpdateAccountDeltaSynchronizationIdentifierAsync(Guid accountId, string synchronizationDeltaIdentifier)
            => _accountService.UpdateSynchronizationIdentifierAsync(accountId, synchronizationDeltaIdentifier);

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

        // Folder methods
        public Task UpdateFolderStructureAsync(Guid accountId, List<MailItemFolder> allFolders)
            => FolderService.BulkUpdateFolderStructureAsync(accountId, allFolders);

        public Task<bool> MapLocalDraftAsync(Guid accountId, Guid localDraftCopyUniqueId, string newMailCopyId, string newDraftId, string newThreadId)
            => MailService.MapLocalDraftAsync(accountId, localDraftCopyUniqueId, newMailCopyId, newDraftId, newThreadId);



        public Task<List<MailItemFolder>> GetSynchronizationFoldersAsync(SynchronizationOptions options)
            => FolderService.GetSynchronizationFoldersAsync(options);

        public Task DeleteFolderAsync(Guid accountId, string remoteFolderId)
            => FolderService.DeleteFolderAsync(accountId, remoteFolderId);

        public Task InsertFolderAsync(MailItemFolder folder)
            => FolderService.InsertFolderAsync(folder);

        public Task<List<MailCopy>> GetDownloadedUnreadMailsAsync(Guid accountId, IEnumerable<string> downloadedMailCopyIds)
            => MailService.GetDownloadedUnreadMailsAsync(accountId, downloadedMailCopyIds);



        public Task SaveMimeFileAsync(Guid fileId, MimeMessage mimeMessage, Guid accountId)
            => _mimeFileService.SaveMimeMessageAsync(fileId, mimeMessage, accountId);

        public Task UpdateFolderLastSyncDateAsync(Guid folderId)
            => FolderService.UpdateFolderLastSyncDateAsync(folderId);
    }
}
