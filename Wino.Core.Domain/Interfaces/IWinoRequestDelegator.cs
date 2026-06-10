using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Domain.Interfaces;

[Wino.Core.Domain.Attributes.WinoRpcService]
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
    Task ExecuteAsync(DraftPreparationRequest draftPreperationRequest);

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

    /// <summary>
    /// Prepares and queues calendar action requests for proper synchronizers.
    /// </summary>
    /// <param name="calendarOperationPreparationRequest">Calendar preparation request.</param>
    Task ExecuteAsync(CalendarOperationPreparationRequest calendarOperationPreparationRequest);

    /// <summary>
    /// Queues a remote category create/update/delete operation for synchronization.
    /// </summary>
    /// <param name="categoryOperationRequest">Serializable category change descriptor.</param>
    Task ExecuteAsync(MailCategoryOperationRequest categoryOperationRequest);

    /// <summary>
    /// Queues remote category assignment changes for a batch of mails.
    /// </summary>
    /// <param name="categoryAssignmentRequest">Serializable category assignment descriptor.</param>
    Task ExecuteAsync(MailCategoryAssignmentOperationRequest categoryAssignmentRequest);

    /// <summary>
    /// Queues pre-built requests for a single account and triggers synchronization.
    /// Companion-internal: IRequestBase implementations are not serializable, so this
    /// overload never crosses the pipe. UI code must use the descriptor overloads above.
    /// </summary>
    [Wino.Core.Domain.Attributes.WinoRpcExclude]
    Task ExecuteAsync(Guid accountId, IEnumerable<IRequestBase> requests);
}
