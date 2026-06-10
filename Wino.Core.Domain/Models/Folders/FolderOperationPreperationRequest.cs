using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Folders;

/// <summary>
/// Encapsulates a request to prepare a folder operation like Rename, Delete, etc.
/// Crosses the RPC pipe, so any user input the operation needs (new name, delete
/// confirmation) must be collected in the UI process and carried here; the companion
/// cannot show dialogs.
/// </summary>
/// <param name="Action">Folder operation.</param>
/// <param name="Folder">Target folder.</param>
/// <param name="UserInput">User-entered text for Rename/CreateSubFolder/CreateRootFolder.</param>
/// <param name="IsConfirmed">User confirmation for destructive operations like Delete.</param>
public record FolderOperationPreperationRequest(FolderOperation Action, MailItemFolder Folder, string UserInput = null, bool IsConfirmed = false) { }
