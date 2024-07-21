using System.Threading.Tasks;
using Wino.Domain.Interfaces;

namespace Wino.Services.Processors
{
    public class GmailChangeProcessor : DefaultChangeProcessor, IGmailChangeProcessor
    {
        public GmailChangeProcessor(IDatabaseService databaseService, IFolderService folderService, IMailService mailService, IAccountService accountService, IMimeFileService mimeFileService) : base(databaseService, folderService, mailService, accountService, mimeFileService)
        {
        }

        public Task MapLocalDraftAsync(string mailCopyId, string newDraftId, string newThreadId)
            => MailService.MapLocalDraftAsync(mailCopyId, newDraftId, newThreadId);
    }
}
