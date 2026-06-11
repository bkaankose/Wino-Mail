using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// MIME parsing and rendering for the UI. All MimeKit work happens in the background
/// companion: messages are rendered to HTML (inline resources written to the shared MIME
/// resource directory), attachments are extracted to files on demand, and drafts are
/// edited through a serializable content model. The UI process never parses .eml.
/// </summary>
[Wino.Core.Domain.Attributes.WinoRpcService]
public interface IMailRenderService
{
    /// <summary>
    /// Renders a stored message. S/MIME protected messages are decrypted/verified first
    /// (see ISmimeService); the returned model carries the resulting signature info.
    /// </summary>
    Task<MailRenderInfo> RenderMailAsync(Guid fileId, Guid accountId, MailRenderingOptions options);

    /// <summary>
    /// Renders a standalone .eml file (file activation); no account context.
    /// </summary>
    Task<MailRenderInfo> RenderEmlFileAsync(string emlFilePath, MailRenderingOptions options);

    /// <summary>
    /// Extracts the attachment at the given render-model index to a file in the message's
    /// MIME resource directory and returns its full path.
    /// </summary>
    Task<string> ExtractAttachmentAsync(Guid fileId, Guid accountId, int attachmentIndex);

    /// <summary>
    /// Extracts an attachment of a standalone .eml file to a temp location and returns
    /// its full path.
    /// </summary>
    Task<string> ExtractEmlFileAttachmentAsync(string emlFilePath, int attachmentIndex);

    /// <summary>
    /// Returns the raw text/calendar (ICS) content of a calendar invitation mail,
    /// or null when the message has none.
    /// </summary>
    Task<string> GetCalendarInvitationIcsAsync(Guid fileId, Guid accountId);

    /// <summary>
    /// Loads the editable content of a draft. Existing attachments are extracted to files
    /// so the UI can list/reattach them without touching the MIME.
    /// </summary>
    Task<MailDraftContent> GetDraftContentAsync(Guid fileId, Guid accountId);

    /// <summary>
    /// Applies the edited content onto the stored draft MIME (preserving identification
    /// headers), saves it, and updates the draft's database record (subject, preview,
    /// sender, attachment flag).
    /// </summary>
    Task SaveDraftContentAsync(Guid mailCopyUniqueId, MailDraftContent content);
}
