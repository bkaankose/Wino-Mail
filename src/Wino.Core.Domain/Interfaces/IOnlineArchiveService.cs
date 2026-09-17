using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.PublicFolders;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Read-only access to the user's Exchange online archive (the in-place archive mailbox). Like public folders,
/// the server is the source of truth: the archive folder tree is walked lazily and mail is fetched live on
/// open. Nothing is persisted into the mailbox tables. Archive folders are mail folders, so the kind-neutral
/// <see cref="PublicFolderNode"/> is reused. Exchange only.
/// </summary>
public interface IOnlineArchiveService
{
    /// <summary>Whether the account could expose an online archive. Cheap: no network or synchronizer resolution.</summary>
    bool SupportsOnlineArchive(MailAccount account);

    /// <summary>
    /// Top-level folders of the archive mailbox. Returns null when no archive is provisioned for the mailbox
    /// (so the UI can show "not enabled" rather than a load error); an empty list means an empty archive.
    /// </summary>
    Task<IReadOnlyList<PublicFolderNode>> GetRootFoldersAsync(Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>Direct children of an archive folder (lazy expand on demand).</summary>
    Task<IReadOnlyList<PublicFolderNode>> GetChildrenAsync(Guid accountId, string parentFolderId, CancellationToken cancellationToken = default);

    /// <summary>A page of mail items in an archive folder, as transient (never persisted) copies.</summary>
    Task<IReadOnlyList<MailCopy>> GetMailItemsAsync(Guid accountId, string folderId, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Raw MIME of a single archive message, for the reading pane.</summary>
    Task<byte[]> GetMailMimeAsync(Guid accountId, string folderId, string itemId, CancellationToken cancellationToken = default);
}
