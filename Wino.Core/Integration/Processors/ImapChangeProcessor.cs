using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;

namespace Wino.Core.Integration.Processors
{
    public class ImapChangeProcessor : DefaultChangeProcessor, IImapChangeProcessor
    {
        public ImapChangeProcessor(IDatabaseService databaseService,
                                   IFolderService folderService,
                                   IMailService mailService,
                                   IAccountService accountService,
                                   IMimeFileService mimeFileService) : base(databaseService, folderService, mailService, accountService, mimeFileService)
        {
        }

        public Task<IList<uint>> GetKnownUidsForFolderAsync(Guid folderId)
            => FolderService.GetKnownUidsForFolderAsync(folderId);

        public Task<List<MailItemFolder>> GetLocalIMAPFoldersAsync(Guid accountId)
            => FolderService.GetFoldersAsync(accountId);

        public Task UpdateFolderAsync(MailItemFolder folder)
            => FolderService.UpdateFolderAsync(folder);
    }
}
