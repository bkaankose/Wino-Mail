using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

    public interface IThumbnailService
{
    string GetHost(string address);
    Task<string> TryGetThumbnailsCacheDirectory();
    Task<string> TryGetGravatarBase64Async(string email);
    Task<string> TryGetFaviconBase64Async(string domain);
}
