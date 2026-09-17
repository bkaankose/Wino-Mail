using System;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.MenuItems;

namespace Wino.Core.Domain.Models.PublicFolders;

/// <summary>
/// Builds the transient calendar row that stands for a pinned public calendar folder in Calendar: read-only,
/// never synchronized, drawn in the favourite's colour. Its id is derived from the account and the folder,
/// so the pane and the loaded events keep referring to the same calendar across rebuilds.
/// </summary>
public static class PublicFolderCalendarFactory
{
    private const string FallbackColorHex = "#0F8A8A";

    public static Guid GetCalendarId(Guid accountId, string folderId)
        => PublicFolderMenuItemFactory.DeterministicId(accountId, "public-calendar:" + folderId);

    public static AccountCalendar Create(PublicFolderFavorite favorite)
    {
        ArgumentNullException.ThrowIfNull(favorite);

        return new AccountCalendar
        {
            Id = GetCalendarId(favorite.AccountId, favorite.FolderId),
            AccountId = favorite.AccountId,
            RemoteCalendarId = favorite.FolderId,
            Name = favorite.DisplayName,
            IsPrimary = false,
            IsReadOnly = true,
            IsSynchronizationEnabled = false,
            IsExtended = favorite.IsChecked,
            BackgroundColorHex = string.IsNullOrWhiteSpace(favorite.ColorHex) ? FallbackColorHex : favorite.ColorHex,
            TextColorHex = "#FFFFFF",
            IsPublicFolder = true
        };
    }
}
