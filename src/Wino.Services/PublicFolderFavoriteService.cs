using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PublicFolders;
using Wino.Messaging.UI;

namespace Wino.Services;

/// <summary>
/// Stores the user's public folder favourites and the remote-tree visibility toggles as small values in the
/// configuration store, the only persisted public folder state. Low contention (user-driven changes), so no locking.
/// </summary>
public class PublicFolderFavoriteService : IPublicFolderFavoriteService
{
    private const string FavoritesKey = "PublicFolderFavorites";
    private const string PublicFoldersVisibleKey = "PublicFoldersRootVisible";
    private const string OnlineArchiveVisibleKey = "OnlineArchiveRootVisible";

    private readonly IConfigurationService _configurationService;

    public PublicFolderFavoriteService(IConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    public IReadOnlyList<PublicFolderFavorite> GetFavorites()
    {
        var json = _configurationService.Get<string>(FavoritesKey, null);
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<PublicFolderFavorite>();

        try
        {
            return JsonSerializer.Deserialize(json, PublicFolderFavoriteJsonContext.Default.ListPublicFolderFavorite) ?? new List<PublicFolderFavorite>();
        }
        catch (JsonException)
        {
            return Array.Empty<PublicFolderFavorite>();
        }
    }

    public bool IsFavorite(Guid accountId, string folderId)
        => GetFavorites().Any(f => f.AccountId == accountId && string.Equals(f.FolderId, folderId, StringComparison.Ordinal));

    public void AddFavorite(PublicFolderFavorite favorite)
    {
        if (favorite == null || string.IsNullOrEmpty(favorite.FolderId))
            return;

        var list = GetFavorites().ToList();
        if (list.Any(f => f.AccountId == favorite.AccountId && string.Equals(f.FolderId, favorite.FolderId, StringComparison.Ordinal)))
            return;

        list.Add(favorite);
        Save(list);

        WeakReferenceMessenger.Default.Send(new PublicFolderFavoritesChanged(favorite.AccountId, favorite.Kind));
    }

    public void RemoveFavorite(Guid accountId, string folderId)
    {
        var list = GetFavorites().ToList();
        var removed = list.Where(f => f.AccountId == accountId && string.Equals(f.FolderId, folderId, StringComparison.Ordinal)).ToList();

        if (removed.Count == 0)
            return;

        list.RemoveAll(removed.Contains);
        Save(list);

        foreach (var kind in removed.Select(f => f.Kind).Distinct())
        {
            WeakReferenceMessenger.Default.Send(new PublicFolderFavoritesChanged(accountId, kind));
        }
    }

    public bool ArePublicFoldersVisible
    {
        get => _configurationService.Get(PublicFoldersVisibleKey, false);
        set => _configurationService.Set(PublicFoldersVisibleKey, value);
    }

    public bool AreOnlineArchivesVisible
    {
        get => _configurationService.Get(OnlineArchiveVisibleKey, false);
        set => _configurationService.Set(OnlineArchiveVisibleKey, value);
    }

    private void Save(List<PublicFolderFavorite> favorites)
        => _configurationService.Set(FavoritesKey, JsonSerializer.Serialize(favorites, PublicFolderFavoriteJsonContext.Default.ListPublicFolderFavorite));
}
