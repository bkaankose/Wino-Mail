using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Ipc;

/// <summary>
/// Implemented by the generated dispatcher in the background service. Receives the parsed
/// request payload and returns the UTF-8 JSON bytes of the response record (or null for
/// void methods). Exceptions thrown here are converted to error envelopes by the server.
/// </summary>
public interface IRpcRequestHandler
{
    Task<byte[]?> HandleRequestAsync(string methodName, JsonElement payload, CancellationToken cancellationToken);
}
