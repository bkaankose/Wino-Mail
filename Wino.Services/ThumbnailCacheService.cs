using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class ThumbnailCacheService : BaseDatabaseService, IThumbnailCacheService
{
    public ThumbnailCacheService(IDatabaseService databaseService) : base(databaseService)
    {
    }

    public Task<Thumbnail> GetThumbnailAsync(string email)
        => Connection.Table<Thumbnail>().Where(thumbnail => thumbnail.Domain == email).FirstOrDefaultAsync();

    public Task SaveThumbnailAsync(Thumbnail thumbnail)
        => Connection.InsertOrReplaceAsync(thumbnail, typeof(Thumbnail));

    public Task DeleteThumbnailAsync(string email)
        => Connection.ExecuteAsync($"DELETE FROM {nameof(Thumbnail)} WHERE {nameof(Thumbnail.Domain)} = ?", email);

    public Task ClearAllThumbnailsAsync()
        => Connection.DeleteAllAsync<Thumbnail>();
}
