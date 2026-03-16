#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoAccountProfileService
{
    Task<WinoAccountOperationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<WinoAccountOperationResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<WinoAccountOperationResult> RefreshAsync(CancellationToken cancellationToken = default);
    Task<WinoAccount?> GetActiveAccountAsync();
    Task<bool> HasActiveAccountAsync();
    Task SignOutAsync(CancellationToken cancellationToken = default);
}
