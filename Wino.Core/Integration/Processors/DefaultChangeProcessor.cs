﻿using System;
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
    /// <see cref="IGmailChangeProcessor"/> and <see cref="IOutlookChangeProcessor"/>
    /// None of the synchronizers can directly change anything in the database.
    /// </summary>
    public interface IDefaultChangeProcessor
    {
        Task<string> UpdateAccountDeltaSynchronizationIdentifierAsync(Guid accountId, string deltaSynchronizationIdentifier);
        Task<string> UpdateFolderDeltaSynchronizationIdentifierAsync(Guid folderId, string deltaSynchronizationIdentifier);

        Task CreateAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId);
        Task DeleteAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId);

        Task ChangeMailReadStatusAsync(string mailCopyId, bool isRead);
        Task ChangeFlagStatusAsync(string mailCopyId, bool isFlagged);

        Task<bool> CreateMailAsync(Guid AccountId, NewMailItemPackage package);
        Task DeleteMailAsync(Guid accountId, string mailId);

        Task<bool> MapLocalDraftAsync(Guid accountId, Guid localDraftCopyUniqueId, string newMailCopyId, string newDraftId, string newThreadId);
        Task MapLocalDraftAsync(string mailCopyId, string newDraftId, string newThreadId);

        Task<List<MailCopy>> GetDownloadedUnreadMailsAsync(Guid accountId, IEnumerable<string> downloadedMailCopyIds);

        Task SaveMimeFileAsync(Guid fileId, MimeMessage mimeMessage, Guid accountId);

        // For Gmail and IMAP.
        Task BulkUpdateFolderStructureAsync(Guid accountId, List<MailItemFolder> allFolders);

        Task DeleteFolderAsync(Guid accountId, string remoteFolderId);
        Task<List<MailItemFolder>> GetSynchronizationFoldersAsync(SynchronizationOptions options);
        Task InsertFolderAsync(MailItemFolder folder);

        Task<IList<uint>> GetKnownUidsForFolderAsync(Guid folderId);
        Task<List<MailItemFolder>> GetExistingFoldersAsync(Guid accountId);
    }

    public interface IGmailChangeProcessor : IDefaultChangeProcessor
    {

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
    }

    public class DefaultChangeProcessor(IDatabaseService databaseService,
                                  IFolderService folderService,
                                  IMailService mailService,
                                  IAccountService accountService,
                                  IMimeFileService mimeFileService) : BaseDatabaseService(databaseService), IDefaultChangeProcessor
    {
        protected IMailService MailService = mailService;

        private readonly IFolderService _folderService = folderService;
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
        public Task BulkUpdateFolderStructureAsync(Guid accountId, List<MailItemFolder> allFolders)
            => _folderService.BulkUpdateFolderStructureAsync(accountId, allFolders);

        public Task<List<MailItemFolder>> GetExistingFoldersAsync(Guid accountId)
            => _folderService.GetFoldersAsync(accountId);

        public Task<bool> MapLocalDraftAsync(Guid accountId, Guid localDraftCopyUniqueId, string newMailCopyId, string newDraftId, string newThreadId)
            => MailService.MapLocalDraftAsync(accountId, localDraftCopyUniqueId, newMailCopyId, newDraftId, newThreadId);

        public Task MapLocalDraftAsync(string mailCopyId, string newDraftId, string newThreadId)
            => MailService.MapLocalDraftAsync(mailCopyId, newDraftId, newThreadId);

        public Task<List<MailItemFolder>> GetSynchronizationFoldersAsync(SynchronizationOptions options)
            => _folderService.GetSynchronizationFoldersAsync(options);

        public Task<string> UpdateFolderDeltaSynchronizationIdentifierAsync(Guid folderId, string deltaSynchronizationIdentifier)
            => _folderService.UpdateFolderDeltaSynchronizationIdentifierAsync(folderId, deltaSynchronizationIdentifier);

        public Task DeleteFolderAsync(Guid accountId, string remoteFolderId)
            => _folderService.DeleteFolderAsync(accountId, remoteFolderId);

        public Task InsertFolderAsync(MailItemFolder folder)
            => _folderService.InsertFolderAsync(folder);

        public Task<List<MailCopy>> GetDownloadedUnreadMailsAsync(Guid accountId, IEnumerable<string> downloadedMailCopyIds)
            => MailService.GetDownloadedUnreadMailsAsync(accountId, downloadedMailCopyIds);

        public Task<IList<uint>> GetKnownUidsForFolderAsync(Guid folderId)
            => _folderService.GetKnownUidsForFolderAsync(folderId);

        public Task SaveMimeFileAsync(Guid fileId, MimeMessage mimeMessage, Guid accountId)
            => _mimeFileService.SaveMimeMessageAsync(fileId, mimeMessage, accountId);
    }
}
