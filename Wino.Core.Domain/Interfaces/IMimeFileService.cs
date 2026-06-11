using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// MIME resource path and cache file access. MimeKit-free on purpose: the UI process keeps an
/// in-process implementation for path/existence checks and html/summary caches. Parsing and
/// writing of the actual MimeMessage is companion-only (IMimeFileServiceInternal in Wino.Services).
/// </summary>
public interface IMimeFileService
{
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
    /// Deletes the given mime file from the disk.
    /// </summary>
    Task<bool> DeleteMimeMessageAsync(Guid accountId, Guid fileId);

    /// <summary>
    /// Returns cached translated html for the given mime resource if it exists.
    /// </summary>
    Task<string> GetTranslatedHtmlAsync(Guid accountId, Guid fileId, string targetLanguage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves translated html for the given mime resource.
    /// </summary>
    Task SaveTranslatedHtmlAsync(Guid accountId, Guid fileId, string targetLanguage, string html, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns cached summary text for the given mime resource if it exists.
    /// </summary>
    Task<string> GetSummaryTextAsync(Guid accountId, Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves summary text for the given mime resource.
    /// </summary>
    Task SaveSummaryTextAsync(Guid accountId, Guid fileId, string summary, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every file in the mime cache for the given account.
    /// </summary>
    /// <param name="accountId">Account id.</param>
    Task DeleteUserMimeCacheAsync(Guid accountId);
}
