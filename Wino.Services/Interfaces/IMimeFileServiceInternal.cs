using System;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Reader;

namespace Wino.Services;

/// <summary>
/// Companion-process-only MIME surface of <see cref="IMimeFileService"/>. These members carry
/// MimeKit types and therefore cannot live in Wino.Core.Domain, which the AOT UI references.
/// </summary>
public interface IMimeFileServiceInternal : IMimeFileService
{
    /// <summary>
    /// Finds the EML file for the given mail id for address, parses and returns MimeMessage.
    /// </summary>
    Task<MimeMessageInformation> GetMimeMessageInformationAsync(Guid fileId, Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the mime message information for the given EML file bytes.
    /// This override is used when EML file association launch is used
    /// because we may not have the access to the file path.
    /// </summary>
    Task<MimeMessageInformation> GetMimeMessageInformationAsync(byte[] fileBytes, string emlFilePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves EML file to the disk.
    /// </summary>
    Task<bool> SaveMimeMessageAsync(Guid fileId, MimeMessage mimeMessage, Guid accountId);

    /// <summary>
    /// Creates HtmlPreviewVisitor for the given MimeMessage.
    /// </summary>
    HtmlPreviewVisitor CreateHTMLPreviewVisitor(MimeMessage message, string mimeLocalPath);

    /// <summary>
    /// Prepares the final model containing rendering details.
    /// </summary>
    MailRenderModel GetMailRenderModel(MimeMessage message, string mimeLocalPath, MailRenderingOptions options = null);
}
