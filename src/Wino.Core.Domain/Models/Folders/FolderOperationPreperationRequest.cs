using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Folders;

/// <summary>
/// Encapsulates a request to prepare a folder operation like Rename, Delete, etc.
/// </summary>
/// <param name="Action">Folder operation.</param>
/// <param name="Folder">Target folder.</param>
/// <param name="NewFolderName">New server name for Rename; null asks the user.</param>
public record FolderOperationPreperationRequest(FolderOperation Action, MailItemFolder Folder, string? NewFolderName = null) { }
