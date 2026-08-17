using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Mail;

/// <summary>
/// Folder navigation settings that arrived from a Wino Account import before the folder itself existed locally.
/// A freshly imported account has no folders until it is re-authenticated and synchronized, so the desired
/// layout is parked here and consumed by <see cref="Interfaces.IFolderService.InsertFolderAsync"/> as the
/// synchronizers create the folders. Rows are deleted as soon as they are applied.
/// </summary>
public class FolderConfigurationOverride
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public Guid MailAccountId { get; set; }

    /// <summary>
    /// Provider folder identifier. Local folder ids are regenerated per device, so this is the only usable key.
    /// </summary>
    public string RemoteFolderId { get; set; }

    public bool IsSticky { get; set; }
    public bool IsHidden { get; set; }
    public int Order { get; set; }
    public bool ShowUnreadCount { get; set; }
    public bool IsJumpListEnabled { get; set; }
}
