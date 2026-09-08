using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Services;

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

        await mails.SaveDraftMetadataAsync(snapshot.AccountId, metadata).ConfigureAwait(false);
        updates.Schedule(snapshot);
        return true;
    }
}
