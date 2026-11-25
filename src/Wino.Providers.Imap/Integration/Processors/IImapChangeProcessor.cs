using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MimeKit;
using Wino.Core.Integration.Processors;

namespace Wino.Providers.Imap.Integration.Processors;

public interface IImapChangeProcessor : ICalDavChangeProcessor
{
    /// <summary>
    /// Returns all known uids for the given folder.
    /// </summary>
    /// <param name="folderId">Folder id to retrieve uIds for.</param>
    Task<IList<uint>> GetKnownUidsForFolderAsync(Guid folderId);

    /// <summary>
    /// Replaces a stored subject or sender name that carries '?' or U+FFFD with the value from the message's
    /// downloaded MIME when the two differ. Rows synchronized before the header section was re-read can hold
    /// text a server downgraded; the MIME is the stored message and is authoritative. Drafts are left alone.
    /// </summary>
    Task RepairDowngradedHeaderTextAsync(Guid mailUniqueId, MimeMessage message);
}
