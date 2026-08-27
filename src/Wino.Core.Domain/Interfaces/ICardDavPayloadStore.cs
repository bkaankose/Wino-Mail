using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface ICardDavPayloadStore
{
    Task<string> SaveAsync(string content, CancellationToken cancellationToken = default);
    Task<string> ReadAsync(string reference, CancellationToken cancellationToken = default);
    Task DeleteUnreferencedAsync(IReadOnlySet<string> referencedPayloads, CancellationToken cancellationToken = default);
}
