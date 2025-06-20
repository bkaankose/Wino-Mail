using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface IThumbnailService
{
    Task ClearCache();
    ValueTask<string> GetThumbnail(string email);
}
