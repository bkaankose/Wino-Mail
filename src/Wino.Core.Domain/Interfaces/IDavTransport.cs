using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Core.Domain.Interfaces;

public interface IDavTransport
{
    Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        DavAuthenticationProfile authentication,
        CancellationToken cancellationToken = default);
}
