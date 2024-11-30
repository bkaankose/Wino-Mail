using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Services;

namespace Wino.Core.Integration.Processors
{
    public class OutlookChangeProcessor(IDatabaseService databaseService,
                                        IFolderService folderService,
                                        IMailService mailService,
                                        IAccountService accountService,
                                        IMimeFileService mimeFileService) : DefaultChangeProcessor(databaseService, folderService, mailService, accountService, mimeFileService)
        , IOutlookChangeProcessor
    {
        public Task<bool> IsMailExistsAsync(string messageId)
            => MailService.IsMailExistsAsync(messageId);

        public Task<bool> IsMailExistsInFolderAsync(string messageId, Guid folderId)
            => MailService.IsMailExistsAsync(messageId, folderId);

        public Task<string> ResetAccountDeltaTokenAsync(Guid accountId)
            => AccountService.UpdateSynchronizationIdentifierAsync(accountId, null);

        public async Task<string> ResetFolderDeltaTokenAsync(Guid folderId)
        {
            var folder = await FolderService.GetFolderAsync(folderId);

            folder.DeltaToken = null;

            await FolderService.UpdateFolderAsync(folder);

            return string.Empty;
        }

        public Task UpdateFolderDeltaSynchronizationIdentifierAsync(Guid folderId, string synchronizationIdentifier)
            => Connection.ExecuteAsync("UPDATE MailItemFolder SET DeltaToken = ? WHERE Id = ?", synchronizationIdentifier, folderId);
    }
}
