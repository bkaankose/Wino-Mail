using System.Threading.Tasks;
using Wino.Domain.Entities;

namespace Wino.Domain.Interfaces
{
    public interface IImapTestService
    {
        Task TestImapConnectionAsync(CustomServerInformation serverInformation);
    }
}
