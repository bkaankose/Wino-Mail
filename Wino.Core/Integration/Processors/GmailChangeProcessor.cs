using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Services;

namespace Wino.Core.Integration.Processors
{
    public class GmailChangeProcessor : DefaultChangeProcessor, IGmailChangeProcessor
    {
        public GmailChangeProcessor(IDatabaseService databaseService, IFolderService folderService, IMailService mailService, IAccountService accountService, IMimeFileService mimeFileService) : base(databaseService, folderService, mailService, accountService, mimeFileService)
        {
        }

        public Task MapLocalDraftAsync(string mailCopyId, string newDraftId, string newThreadId)
            => MailService.MapLocalDraftAsync(mailCopyId, newDraftId, newThreadId);

        public Task CreateAssignmentAsync(Guid accountId, string mailCopyId, string remoteFolderId)
            => MailService.CreateAssignmentAsync(accountId, mailCopyId, remoteFolderId);
    }
}
