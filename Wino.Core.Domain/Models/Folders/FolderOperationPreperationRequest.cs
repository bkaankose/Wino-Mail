using Wino.Domain.Entities;
using Wino.Domain.Enums;

namespace Wino.Domain.Models.Folders
{
    /// <summary>
    /// Encapsulates a request to prepare a folder operation like Rename, Delete, etc.
    /// </summary>
    /// <param name="Action">Folder operation.</param>
    /// <param name="Folder">Target folder.</param>
    public record FolderOperationPreperationRequest(FolderOperation Action, MailItemFolder Folder) { }
}
