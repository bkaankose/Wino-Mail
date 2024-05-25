using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;

namespace Wino.Core.Integration.Processors
{
    public class OutlookChangeProcessor(IDatabaseService databaseService,
                                        IFolderService folderService,
                                        IMailService mailService,
                                        IAccountService accountService,
                                        IMimeFileService mimeFileService) : DefaultChangeProcessor(databaseService, folderService, mailService, accountService, mimeFileService), IOutlookChangeProcessor
    {
        public Task<bool> IsMailExistsAsync(string messageId)
            => MailService.IsMailExistsAsync(messageId);
    }
}
