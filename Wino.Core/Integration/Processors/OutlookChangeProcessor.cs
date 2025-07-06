using System;
using System.Threading.Tasks;
using Microsoft.Graph.Models;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Services;

namespace Wino.Core.Integration.Processors;

public class OutlookChangeProcessor(IDatabaseService databaseService,
                                    IFolderService folderService,
                                    ICalendarService calendarService,
                                    IMailService mailService,
                                    IAccountService accountService,
                                    ICalendarServiceEx calendarServiceEx,
                                    IMimeFileService mimeFileService) : DefaultChangeProcessor(databaseService, folderService, mailService, calendarService, accountService, calendarServiceEx, mimeFileService)
    , IOutlookChangeProcessor
{

    public Task<string> ResetAccountDeltaTokenAsync(Guid accountId)
        => AccountService.UpdateSyncIdentifierRawAsync(accountId, string.Empty);

    public async Task<string> ResetFolderDeltaTokenAsync(Guid folderId)
    {
        var folder = await FolderService.GetFolderAsync(folderId);

        folder.DeltaToken = null;

        await FolderService.UpdateFolderAsync(folder);

        return string.Empty;
    }

    public Task UpdateFolderDeltaSynchronizationIdentifierAsync(Guid folderId, string synchronizationIdentifier)
        => Connection.ExecuteAsync("UPDATE MailItemFolder SET DeltaToken = ? WHERE Id = ?", synchronizationIdentifier, folderId);

    public async Task ManageCalendarEventAsync(Event calendarEvent, AccountCalendar assignedCalendar, MailAccount organizerAccount)
    {
        // TODO
    }
}
