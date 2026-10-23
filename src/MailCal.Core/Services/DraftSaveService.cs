using System.Threading.Tasks;
using Serilog;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.MailItem;

namespace MailCal.Core.Services;

public sealed class DraftSaveService(IMimeFileService mimeFiles, IMailService mails,
    IDraftUpdateCoordinator updates, DraftUpdateRegistry registry) : IDraftSaveService
{
    public async Task<bool> SaveAsync(DraftUpdateSnapshot snapshot, MailCopy metadata)
    {
        var wasProtected = registry.IsProtected(snapshot.AccountId, metadata.UniqueId);
        registry.Protect(snapshot.AccountId, metadata, snapshot.Revision);
        using var mime = snapshot.OpenMime();
        if (!await mimeFiles.SaveDraftMimeMessageAsync(metadata.FileId, mime, snapshot.AccountId).ConfigureAwait(false))
        {
            if (!wasProtected) registry.Complete(snapshot.AccountId, metadata.UniqueId, snapshot.Revision);
            return false;
        }

        if (!await mails.SaveDraftMetadataAsync(snapshot.AccountId, metadata).ConfigureAwait(false))
        {
            // The draft is gone (deleted, sent or moved). There is nothing to push to the server.
            if (!wasProtected) registry.Complete(snapshot.AccountId, metadata.UniqueId, snapshot.Revision);
            Log.Warning("Draft {UniqueId} was not saved because it no longer exists.", metadata.UniqueId);
            return false;
        }

        updates.Schedule(snapshot);
        return true;
    }
}
