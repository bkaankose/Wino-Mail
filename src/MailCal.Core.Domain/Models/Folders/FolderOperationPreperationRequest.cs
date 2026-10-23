using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Models.Folders;

/// <summary>
/// Encapsulates a request to prepare a folder operation like Rename, Delete, etc.
/// </summary>
/// <param name="Action">Folder operation.</param>
/// <param name="Folder">Target folder.</param>
public record FolderOperationPreperationRequest(FolderOperation Action, MailItemFolder Folder) { }
