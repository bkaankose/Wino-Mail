using CommunityToolkit.Mvvm.Messaging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Services;

namespace Wino.Core.Integration.Processors;

public class ImapChangeProcessor : CalDavChangeProcessor, IImapChangeProcessor
{
    public ImapChangeProcessor(IDatabaseService databaseService, IMessenger messenger,
                               IFolderService folderService,
                               IMailService mailService,
                               IAccountService accountService,
                               ICalendarService calendarService,
                               IMimeFileService mimeFileService,
                               ICalendarIcsFileService calendarIcsFileService,
                               IContactService contactService = null,
                               ITaskService taskService = null,
                               IApplicationConfiguration applicationConfiguration = null) : base(databaseService, messenger, folderService, mailService, accountService, calendarService, mimeFileService, calendarIcsFileService, contactService, taskService, applicationConfiguration)
    {
    }

    public Task<IList<uint>> GetKnownUidsForFolderAsync(Guid folderId) => FolderService.GetKnownUidsForFolderAsync(folderId);
}
