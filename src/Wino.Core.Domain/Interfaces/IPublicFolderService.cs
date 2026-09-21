using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.PublicFolders;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Read-only access to Exchange public folders (the organization-wide shared hierarchy). The server is the
/// source of truth: the hierarchy is walked lazily and content is fetched live on open. Nothing is persisted
/// into the mailbox tables, so public folder content never reaches the unified inbox, search or the address
/// book. Resolves the account's synchronizer via <see cref="ISynchronizerFactory"/> and delegates.
/// </summary>
public interface IPublicFolderService
{
    /// <summary>Whether the account could expose public folders. Cheap: no network or synchronizer resolution.</summary>
    bool SupportsPublicFolders(MailAccount account);

    /// <summary>Direct children of the public folders root.</summary>
    Task<IReadOnlyList<PublicFolderNode>> GetRootChildrenAsync(Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>Direct children of a public folder (lazy expand on demand).</summary>
    Task<IReadOnlyList<PublicFolderNode>> GetChildrenAsync(Guid accountId, string parentFolderId, CancellationToken cancellationToken = default);

    /// <summary>A page of mail or post items in a public mail folder, as transient (never persisted) copies.</summary>
    Task<IReadOnlyList<MailCopy>> GetMailItemsAsync(Guid accountId, string folderId, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>Raw MIME of a single public folder message, for the reading pane.</summary>
    Task<byte[]> GetMailMimeAsync(Guid accountId, string folderId, string itemId, CancellationToken cancellationToken = default);

    /// <summary>Appointments in a public calendar folder for a UTC date window, as transient calendar items.</summary>
    Task<IReadOnlyList<CalendarItem>> GetAppointmentsAsync(Guid accountId, string folderId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default);

    /// <summary>Contacts in a public contacts folder, as transient DTOs (not the email-keyed address book).</summary>
    Task<IReadOnlyList<PublicFolderContact>> GetContactsAsync(Guid accountId, string folderId, CancellationToken cancellationToken = default);
}
