using Wino.Domain.Models.Folders;
using Wino.Domain.Models.MailItem;

namespace Wino.Domain.Interfaces
{
    /// <summary>
    /// Prepares server requests and delegates them to proper synchronizers.
    /// This is the last point for sending the server request.
    /// </summary>
    public interface IWinoRequestDelegator
    {
        /// <summary>
        /// Prepares requires IRequest collection for mail actions and executes them via proper synchronizers.
        /// </summary>
        /// <param name="prerperationRequest">Preperation model that encapsulates action and mail items.</param>
        Task QueueAsync(MailOperationPreperationRequest prerperationRequest);

        /// <summary>
        /// Queues new draft creation request for synchronizer.
        /// </summary>
        /// <param name="draftPreperationRequest">A class that holds the parameters for creating a draft.</param>
        Task QueueAsync(DraftPreperationRequest draftPreperationRequest);

        /// <summary>
        /// Queues a new request for synchronizer to send a draft.
        /// </summary>
        /// <param name="draftPreperationRequest">Draft sending request.</param>
        Task QueueAsync(SendDraftPreparationRequest sendDraftPreperationRequest);

        /// <summary>
        /// Prepares required IRequest collection for folder actions and executes them via proper synchronizers.
        /// </summary>
        /// <param name="folderOperationPreperationRequest">Folder prep request.</param>
        Task QueueAsync(FolderOperationPreperationRequest folderOperationPreperationRequest);
    }
}
