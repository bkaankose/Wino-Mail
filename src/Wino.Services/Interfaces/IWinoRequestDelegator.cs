using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Models.MailItem;

namespace Wino.Services.Interfaces;

public interface IWinoRequestDelegator
{
    /// <summary>
    /// Prepares the requests for a mail action and queues them for the synchronizers. Nothing is shown: an error that
    /// stopped the preparation is returned, and an unanswered question (a protected permanent delete, a move without a
    /// destination) comes back as a <see cref="Exceptions.MailOperationDecisionRequiredException"/>. ViewModels call this
    /// through their request coordinator, which asks first and shows the error.
    /// </summary>
    Task<WinoRequestDelegationResult> ExecuteWithResultAsync(MailOperationPreperationRequest preparationRequest);

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
    /// Prepares the request for a folder action and queues it for the synchronizer. The caller has already asked for
    /// any name or confirmation the action needs; an unsupported action comes back as a <see cref="System.NotImplementedException"/>.
    /// </summary>
    Task<WinoRequestDelegationResult> ExecuteAsync(FolderOperationPreperationRequest folderOperationPreperationRequest);

    /// <summary>
    /// Prepares and queues calendar action requests for proper synchronizers. A read-only target calendar queues
    /// nothing and comes back as a <see cref="Exceptions.ReadOnlyCalendarException"/>.
    /// </summary>
    Task<WinoRequestDelegationResult> ExecuteAsync(CalendarOperationPreparationRequest calendarOperationPreparationRequest);

    Task ExecuteAsync(ContactOperationPreparationRequest contactOperationPreparationRequest);
    Task ExecuteAsync(IReadOnlyList<ContactOperationPreparationRequest> preparationRequests);

    /// <summary>
    /// Queues pre-built requests for a single account and publishes the synchronization
    /// request for each represented request type.
    /// </summary>
    Task ExecuteAsync(Guid accountId, IEnumerable<IRequestBase> requests);

    Task ExecuteLocalAsync(IRequestBase request);
}
