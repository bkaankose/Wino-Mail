using System.Threading;
using System.Threading.Tasks;

namespace MailCal.Core.Domain.Interfaces;

public interface IReaderRuntimeService
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
