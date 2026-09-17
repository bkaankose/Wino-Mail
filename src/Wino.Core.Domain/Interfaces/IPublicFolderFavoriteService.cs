using System;
using System.Collections.Generic;
using Wino.Core.Domain.Models.PublicFolders;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Persists the small set of public folders the user pinned, plus the two visibility toggles for the remote
/// read-only trees. This is the only persisted public folder state; hierarchy and content are fetched live.
/// </summary>
public interface IPublicFolderFavoriteService
{
    IReadOnlyList<PublicFolderFavorite> GetFavorites();

    bool IsFavorite(Guid accountId, string folderId);

    /// <summary>Adds the favourite if not already present (no-op otherwise).</summary>
    void AddFavorite(PublicFolderFavorite favorite);

    void RemoveFavorite(Guid accountId, string folderId);

    /// <summary>Remembers whether a pinned public calendar is ticked in the Calendar pane. Announces nothing.</summary>
    void SetFavoriteChecked(Guid accountId, string folderId, bool isChecked);

    /// <summary>
    /// Whether the "Public Folders" tree root is shown under Exchange accounts in the mail navigation.
    /// Defaults to false (hidden) so the user reveals it only to browse or pin folders.
    /// Pinned favourites stay visible regardless of this flag.
    /// </summary>
    bool ArePublicFoldersVisible { get; set; }

    /// <summary>
    /// Whether the read-only "Online Archive" tree root is shown under Exchange accounts in the mail navigation.
    /// Defaults to false (hidden).
    /// </summary>
    bool AreOnlineArchivesVisible { get; set; }
}
