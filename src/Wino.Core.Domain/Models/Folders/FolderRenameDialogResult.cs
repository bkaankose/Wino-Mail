namespace Wino.Core.Domain.Models.Folders;

/// <summary>
/// The server folder name and the local-only display name picked in the rename dialog.
/// An empty display name shows the folder name.
/// </summary>
public sealed record FolderRenameDialogResult(string FolderName, string DisplayName);
