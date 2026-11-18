using CommunityToolkit.Mvvm.Messaging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Wino.Core.Integration.Processors;
using Wino.Services.Interfaces;

namespace Wino.Providers.Imap.Integration.Processors;

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
