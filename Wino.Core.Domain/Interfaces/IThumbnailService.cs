using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface IThumbnailService
{
    Task ClearCache();

    /// <summary>
    /// Gets thumbnail
    /// </summary>
    /// <param name="email">Address for thumbnail</param>
    /// <param name="awaitLoad">Force to wait for thumbnail loading.
    /// Should be used in non-UI threads or where delay is acceptable
    /// </param>
    ValueTask<string> GetThumbnail(string email, bool awaitLoad = false);
}
