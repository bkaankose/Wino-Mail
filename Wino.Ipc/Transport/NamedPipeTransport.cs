using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Ipc.Transport;

/// <summary>
/// The only place named pipes appear; everything above this works on plain streams.
/// </summary>
public static class NamedPipeTransport
{
    public const int MaxServerInstances = 4;

    public static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            await clientStream.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
            return clientStream;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own timeout fired; OperationCanceledException must mean caller
            // cancellation only, otherwise retry loops treat timeouts as fatal.
            await clientStream.DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException($"Timed out connecting to pipe '{pipeName}' after {timeout.TotalMilliseconds:F0} ms.");
        }
        catch
        {
            await clientStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static NamedPipeServerStream CreateServerStream(string pipeName)
        => new(pipeName,
               PipeDirection.InOut,
               MaxServerInstances,
               PipeTransmissionMode.Byte,
               PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
