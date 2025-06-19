using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface IThumbnailService
{
    Task<string> GetAvatarThumbnail(string email);
}
