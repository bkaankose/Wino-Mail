using System.Threading.Tasks;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Interfaces
{
    public interface IWinoRequestDelegator
    {
        /// <summary>
        /// Prepares requires IRequest collection for mail actions and executes them via proper synchronizers.
        /// </summary>
        /// <param name="prerperationRequest">Preperation model that encapsulates action and mail items.</param>
        Task ExecuteAsync(MailOperationPreperationRequest prerperationRequest);

        /// <summary>
        /// Queues new draft creation request for synchronizer.
        /// </summary>
        /// <param name="draftPreperationRequest">A class that holds the parameters for creating a draft.</param>
        Task ExecuteAsync(DraftPreperationRequest draftPreperationRequest);

        /// <summary>
        /// Queues a new request for synchronizer to send a draft.
        /// </summary>
        /// <param name="draftPreperationRequest">Draft sending request.</param>
        Task ExecuteAsync(SendDraftPreparationRequest sendDraftPreperationRequest);

        /// <summary>
        /// Prepares requires IRequest collection for folder actions and executes them via proper synchronizers.
        /// </summary>
        /// <param name="folderOperationPreperationRequest">Folder prep request.</param>
        Task ExecuteAsync(FolderOperationPreperationRequest folderOperationPreperationRequest);
    }
}
