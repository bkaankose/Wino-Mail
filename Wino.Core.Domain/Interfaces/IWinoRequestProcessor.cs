using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Domain.Interfaces
{
    public interface IWinoRequestProcessor
    {
        Task<IRequest> PrepareFolderRequestAsync(FolderOperation operation, IMailItemFolder mailItemFolder);

        /// <summary>
        /// Prepares proper Wino requests for synchronizers to execute categorized by AccountId and FolderId.
        /// </summary>
        /// <param name="operation">User action</param>
        /// <param name="mailCopyIds">Selected mails.</param>
        /// <exception cref="UnavailableSpecialFolderException">When required folder target is not available for account.</exception>
        Task<List<IRequest>> PrepareRequestsAsync(MailOperationPreperationRequest request);
    }
}
