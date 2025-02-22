using System;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Core.Domain.Interfaces;

public interface IMimeFileService
{
    /// <summary>
    /// Finds the EML file for the given mail id for address, parses and returns MimeMessage.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Mime message information</returns>
    Task<MimeMessageInformation> GetMimeMessageInformationAsync(Guid fileId, Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the mime message information for the given EML file bytes.
    /// This override is used when EML file association launch is used
    /// because we may not have the access to the file path.
    /// </summary>
    /// <param name="fileBytes">Byte array of the file.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Mime message information</returns>
    Task<MimeMessageInformation> GetMimeMessageInformationAsync(byte[] fileBytes, string emlFilePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves EML file to the disk.
    /// </summary>
    /// <param name="copy">MailCopy of the native message.</param>
    /// <param name="mimeMessage">MimeMessage that is parsed from native message.</param>
    /// <param name="accountId">Which account Id to save this file for.</param>
    Task<bool> SaveMimeMessageAsync(Guid fileId, MimeMessage mimeMessage, Guid accountId);

    /// <summary>
    /// Returns a path that all Mime resources (including eml) is stored for this MailCopyId
    /// This is useful for storing previously rendered attachments as well.
    /// </summary>
    /// <param name="accountAddress">Account address</param>
    /// <param name="mailCopyId">Resource mail copy id</param>
    Task<string> GetMimeResourcePathAsync(Guid accountId, Guid fileId);

    /// <summary>
    /// Returns whether mime file exists locally or not.
    /// </summary>
    Task<bool> IsMimeExistAsync(Guid accountId, Guid fileId);

    /// <summary>
    /// Creates HtmlPreviewVisitor for the given MimeMessage.
    /// </summary>
    /// <param name="message">Mime</param>
    /// <param name="mimeLocalPath">File path that mime is located to load resources.</param>
    HtmlPreviewVisitor CreateHTMLPreviewVisitor(MimeMessage message, string mimeLocalPath);

    /// <summary>
    /// Deletes the given mime file from the disk.
    /// </summary>
    Task<bool> DeleteMimeMessageAsync(Guid accountId, Guid fileId);

    /// <summary>
    /// Prepares the final model containing rendering details.
    /// </summary>
    /// <param name="message">Message to render.</param>
    /// <param name="mimeLocalPath">File path that physical MimeMessage is located.</param>
    /// <param name="options">Rendering options</param>
    MailRenderModel GetMailRenderModel(MimeMessage message, string mimeLocalPath, MailRenderingOptions options = null);

    /// <summary>
    /// Deletes every file in the mime cache for the given account.
    /// </summary>
    /// <param name="accountId">Account id.</param>
    Task DeleteUserMimeCacheAsync(Guid accountId);
}
