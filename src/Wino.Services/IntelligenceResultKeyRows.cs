#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Intelligence;

namespace Wino.Services;

/// <summary>
/// Raw access to the result key table in the intelligence database. Internal so only
/// <see cref="IntelligenceResultKeyStore"/> ever sees the protected private key column.
/// </summary>
internal interface IIntelligenceResultKeyRows
{
    Task<IReadOnlyList<IntelligenceResultKeyRow>> GetAllAsync(CancellationToken cancellationToken);
    Task<IntelligenceResultKeyRow?> GetAsync(string keyId, CancellationToken cancellationToken);
    Task InsertAsync(IntelligenceResultKeyRow row, CancellationToken cancellationToken);
    Task DeleteAsync(string keyId, CancellationToken cancellationToken);
    Task DeleteAllAsync(CancellationToken cancellationToken);
}
