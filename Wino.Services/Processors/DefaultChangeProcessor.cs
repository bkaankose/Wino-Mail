using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MimeKit;
using Wino.Domain.Entities;
using Wino.Domain.Interfaces;
using Wino.Domain.Models.MailItem;
using Wino.Domain.Models.Synchronization;
using Wino.Services.Services;

namespace Wino.Services.Processors
{
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
    }
}
