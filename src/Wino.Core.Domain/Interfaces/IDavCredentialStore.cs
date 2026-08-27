using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface IDavCredentialStore
{
    Task<string> GetPasswordAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task SavePasswordAsync(Guid accountId, string password, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default);
}
