using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Connectivity;

namespace Wino.Core.Domain.Interfaces;

public interface IPop3TestService
{
    Task<Pop3ConnectivityTestResult> TestConnectionAsync(
        CustomServerInformation serverInformation,
        CancellationToken cancellationToken = default);
}
