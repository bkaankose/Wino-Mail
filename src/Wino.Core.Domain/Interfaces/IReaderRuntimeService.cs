using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

public interface IReaderRuntimeService
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
