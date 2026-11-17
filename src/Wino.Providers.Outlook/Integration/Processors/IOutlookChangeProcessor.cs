using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Integration.Processors;

public interface IOutlookChangeProcessor : IDefaultChangeProcessor
{
    /// <summary>
    /// Updates Folder's delta synchronization identifier.
    /// Only used in Outlook since it does per-folder sync.
    /// </summary>
    /// <param name="folderId">Folder id</param>
    /// <param name="synchronizationIdentifier">New synchronization identifier.</param>
    /// <returns>New identifier if success.</returns>
    Task UpdateFolderDeltaSynchronizationIdentifierAsync(Guid folderId, string deltaSynchronizationIdentifier);

    /// <summary>
    /// Outlook may expire folder's delta token after a while.
    /// Recommended action for this scenario is to reset token and do full sync.
    /// This method resets the token for the given folder.
    /// </summary>
    /// <param name="folderId">Local folder id to reset token for.</param>
    /// <returns>Empty string to assign folder delta sync for.</returns>
    Task<string> ResetFolderDeltaTokenAsync(Guid folderId);

    Task ManageCalendarEventAsync(Microsoft.Graph.Models.Event calendarEvent, AccountCalendar assignedCalendar, MailAccount organizerAccount);
}
