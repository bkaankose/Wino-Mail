using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MailCal.Core.Domain.Interfaces;

public interface IDavResponseHandler
{
    Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken = default);
}
