using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Domain.Models.Folders;
using Wino.Domain.Models.MailItem;

namespace Wino.Domain.Interfaces
{
    public interface IWinoRequestProcessor
    {
        /// <summary>
        /// Prepares proper folder action requests for synchronizers to execute.
        /// </summary>
        /// <param name="request"></param>
        /// <returns>Base request that synchronizer can execute.</returns>
        Task<IRequestBase> PrepareFolderRequestAsync(FolderOperationPreperationRequest request);

        /// <summary>
        /// Prepares proper Wino requests for synchronizers to execute categorized by AccountId and FolderId.
        /// </summary>
        /// <param name="operation">User action</param>
        /// <param name="mailCopyIds">Selected mails.</param>
        /// <exception cref="UnavailableSpecialFolderException">When required folder target is not available for account.</exception>
        /// <returns>Base request that synchronizer can execute.</returns>
        Task<List<IRequest>> PrepareRequestsAsync(MailOperationPreperationRequest request);
    }
}
