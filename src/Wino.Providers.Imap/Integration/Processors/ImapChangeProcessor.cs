using CommunityToolkit.Mvvm.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Interfaces;
using Wino.Services;
using Wino.Services.Extensions;
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

    public async Task RepairDowngradedHeaderTextAsync(Guid mailUniqueId, MimeMessage message)
    {
        if (message == null)
            return;

        var current = await MailService.GetSingleMailItemAsync(mailUniqueId).ConfigureAwait(false);
        if (current == null || current.IsDraft)
            return;

        var subject = message.Subject;
        var fromName = message.From.Mailboxes.FirstOrDefault()?.Name ?? message.Sender?.Name;
        var changed = false;

        if (ShouldRepair(current.Subject, subject))
        {
            current.Subject = subject;
            changed = true;
        }

        if (ShouldRepair(current.FromName, fromName))
        {
            current.FromName = fromName;
            changed = true;
        }

        if (changed)
            await MailService.UpdateMailAsync(current).ConfigureAwait(false);

        static bool ShouldRepair(string stored, string fromMime)
            => !string.IsNullOrWhiteSpace(fromMime)
               && MimeHeaderCharsetRecovery.MayBeDowngraded(stored)
               && !string.Equals(stored, fromMime, StringComparison.Ordinal);
    }
}
