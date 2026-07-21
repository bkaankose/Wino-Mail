using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Attributes;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Small generated control surface used while the database-backed service interfaces
/// are moved behind the companion. It is intentionally free of UI and WinRT types.
/// </summary>
[WinoRpcService]
public interface ICompanionBackendControl
{
    Task<string> GetVersionAsync();
    Task<bool> HasAccountsAsync(CancellationToken cancellationToken = default);
    Task SynchronizeAllAsync(CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
}
