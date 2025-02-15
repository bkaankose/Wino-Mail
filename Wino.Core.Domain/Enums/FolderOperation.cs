namespace Wino.Core.Domain.Enums;

/// <summary>
/// Defines all possible folder operations that can be done.
/// Available values for each folder is returned by IContextMenuProvider
/// that integrators hold.
/// </summary>
public enum FolderOperation
{
    None,
    Pin,
    Unpin,
    MarkAllAsRead,
    DontSync,
    Empty,
    Rename,
    Delete,
    Move,
    TurnOffNotifications,
    CreateSubFolder,
    Seperator
}
