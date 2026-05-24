using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Serilog;
using Wino.Messaging.SyncHost;

namespace Wino.SyncHost;

internal sealed class SyncHostEventPublisher
{
    private readonly ConcurrentDictionary<Guid, StreamWriter> _clients = [];
    private readonly ILogger _logger = Log.ForContext<SyncHostEventPublisher>();

    public void Start(CancellationToken cancellationToken)
        => _ = AcceptLoopAsync(cancellationToken);

    public async Task PublishAsync<TPayload>(string eventType, TPayload payload)
    {
        if (_clients.IsEmpty)
            return;

        var envelope = new SyncHostEventEnvelope(
            SyncHostProtocol.Version,
            Guid.NewGuid(),
            eventType,
            SyncHostJson.ToElement(payload));

        var json = JsonSerializer.Serialize(envelope, SyncHostJson.Options);

        foreach (var (clientId, writer) in _clients.ToArray())
        {
            try
            {
                await writer.WriteLineAsync(json).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Removing disconnected sync host event client {ClientId}.", clientId);

                if (_clients.TryRemove(clientId, out var removedWriter))
                {
                    await removedWriter.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                SyncHostProtocol.EventPipeName,
                PipeDirection.Out,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var writer = new StreamWriter(pipe) { AutoFlush = true };
                _clients[Guid.NewGuid()] = writer;
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                _logger.Error(ex, "Failed to accept sync host event pipe client.");
            }
        }
    }
}
