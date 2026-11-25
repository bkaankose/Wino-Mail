using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Interfaces;

namespace Wino.Mail.Linux.Views.Mail.Compose;

/// <summary>
/// Synchronization stores only the headers and the reader downloads a message when it opens, so a reply or forward
/// started on a mail nobody opened has no message to quote. This downloads it first, as the notification reply does.
/// </summary>
internal sealed class ComposeSourceMime(IMimeFileService mimeFiles, ISynchronizationManager synchronization, IMailDialogService dialogs)
{
    public async Task<bool> EnsureAsync(MailCopy? mail)
    {
        if (mail?.AssignedAccount is not { } account || mail.FileId == Guid.Empty) return false;
        if (await mimeFiles.IsMimeExistAsync(account.Id, mail.FileId).ConfigureAwait(false)) return true;
        try
        {
            await synchronization.DownloadMimeMessageAsync(mail, account.Id).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "Compose source: the message of mail {Id} could not be downloaded.", mail.UniqueId);
        }
        if (await mimeFiles.IsMimeExistAsync(account.Id, mail.FileId).ConfigureAwait(false)) return true;
        dialogs.InfoBarMessage(Translator.Info_DraftCreationFailed, Translator.VoiceResult_MailNotDownloaded, InfoBarMessageType.Error);
        return false;
    }
}
