using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Ipc;

/// <summary>
/// Lets the server accept connections and handshakes immediately while the host process
/// is still initializing (database, translations, …). Requests simply wait until the
/// readiness task completes before being dispatched, so clients never race startup.
/// </summary>
public sealed class InitializationGateHandler : IRpcRequestHandler
{
    private readonly Task _ready;
    private readonly IRpcRequestHandler _innerHandler;

    public InitializationGateHandler(Task ready, IRpcRequestHandler innerHandler)
    {
        _ready = ready;
        _innerHandler = innerHandler;
    }

    public async Task<byte[]?> HandleRequestAsync(string methodName, JsonElement payload, CancellationToken cancellationToken)
    {
        if (!_ready.IsCompleted)
        {
            await _ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return await _innerHandler.HandleRequestAsync(methodName, payload, cancellationToken).ConfigureAwait(false);
    }
}
