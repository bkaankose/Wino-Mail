using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Wino.Core.Integration.Processors;

public interface IImapChangeProcessor : ICalDavChangeProcessor
{
    /// <summary>
    /// Returns all known uids for the given folder.
    /// </summary>
    /// <param name="folderId">Folder id to retrieve uIds for.</param>
    Task<IList<uint>> GetKnownUidsForFolderAsync(Guid folderId);
}
