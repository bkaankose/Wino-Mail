using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Interfaces;

[Wino.Core.Domain.Attributes.WinoRpcService]
public interface IImapTestService
{
    Task TestImapConnectionAsync(CustomServerInformation serverInformation, bool allowSSLHandShake);
}
