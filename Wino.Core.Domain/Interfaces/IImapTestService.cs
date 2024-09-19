using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces
{
    public interface IImapTestService
    {
        Task TestImapConnectionAsync(CustomServerInformation serverInformation, bool allowSSLHandShake);
    }
}
