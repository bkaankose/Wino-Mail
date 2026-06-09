using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Database-backed thumbnail metadata store (gravatar/favicon file names per address).
/// Lives next to the other database services so the UI process never opens SQLite directly.
/// </summary>
[Wino.Core.Domain.Attributes.WinoRpcService]
public interface IThumbnailCacheService
{
    /// <summary>Returns the cached thumbnail entry for the given address or null.</summary>
    Task<Thumbnail> GetThumbnailAsync(string email);

    /// <summary>Inserts or replaces a thumbnail entry.</summary>
    Task SaveThumbnailAsync(Thumbnail thumbnail);

    /// <summary>Deletes the thumbnail entry for the given address.</summary>
    Task DeleteThumbnailAsync(string email);

    /// <summary>Deletes all thumbnail entries.</summary>
    Task ClearAllThumbnailsAsync();
}
