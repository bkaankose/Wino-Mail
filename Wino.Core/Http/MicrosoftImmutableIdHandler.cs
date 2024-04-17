using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Http
{
    /// <summary>
    /// Adds additional Prefer header for immutable id support in the Graph service client.
    /// </summary>
    public class MicrosoftImmutableIdHandler : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("Prefer", "IdType=\"ImmutableId\"");

            return base.SendAsync(request, cancellationToken);
        }
    }
}
