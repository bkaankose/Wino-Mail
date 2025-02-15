﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Services;

namespace Wino.Core.Integration.Processors;

public class ImapChangeProcessor : DefaultChangeProcessor, IImapChangeProcessor
{
    public ImapChangeProcessor(IDatabaseService databaseService,
                               IFolderService folderService,
                               IMailService mailService,
                               IAccountService accountService,
                               ICalendarService calendarService,
                               IMimeFileService mimeFileService) : base(databaseService, folderService, mailService, calendarService, accountService, mimeFileService)
    {
    }

    public Task<IList<uint>> GetKnownUidsForFolderAsync(Guid folderId) => FolderService.GetKnownUidsForFolderAsync(folderId);
}
