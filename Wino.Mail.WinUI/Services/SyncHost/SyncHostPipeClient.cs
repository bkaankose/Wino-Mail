using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Messaging.SyncHost;

namespace Wino.Mail.WinUI.Services.SyncHost;

public sealed class SyncHostPipeClient
{
    private readonly SyncHostProcessLauncher _processLauncher;
    private readonly ILogger _logger = Log.ForContext<SyncHostPipeClient>();

    public SyncHostPipeClient(SyncHostProcessLauncher processLauncher)
    {
        _processLauncher = processLauncher;
    }

    public async Task<TResult?> SendAsync<TPayload, TResult>(
        string commandType,
        TPayload payload,
        CancellationToken cancellationToken = default)
    {
        await _processLauncher.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);

        var response = await SendEnvelopeAsync(commandType, payload, cancellationToken).ConfigureAwait(false);

        if (!response.Success)
        {
            throw new InvalidOperationException(response.ErrorMessage ?? $"Sync host command failed: {commandType}");
        }

        if (response.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return default;

        return SyncHostJson.FromElement<TResult>(response.Payload);
    }

    public Task SendAsync<TPayload>(
        string commandType,
        TPayload payload,
        CancellationToken cancellationToken = default)
        => SendAsync<TPayload, object>(commandType, payload, cancellationToken);

    private async Task<SyncHostResponseEnvelope> SendEnvelopeAsync<TPayload>(
        string commandType,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        var envelope = new SyncHostCommandEnvelope(
            SyncHostProtocol.Version,
            Guid.NewGuid(),
            commandType,
            SyncHostJson.ToElement(payload));

        var json = JsonSerializer.Serialize(envelope, SyncHostJson.Options);

        try
        {
            return await SendJsonAsync(json, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.Warning(ex, "Sync host pipe failed for command {CommandType}; retrying after host launch.", commandType);
            await _processLauncher.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
            return await SendJsonAsync(json, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            _logger.Warning(ex, "Sync host pipe timed out for command {CommandType}; retrying after host launch.", commandType);
            await _processLauncher.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);
            return await SendJsonAsync(json, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<SyncHostResponseEnvelope> SendJsonAsync(string json, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            SyncHostProtocol.CommandPipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        using var reader = new StreamReader(pipe);

        await writer.WriteLineAsync(json.AsMemory(), timeoutCts.Token).ConfigureAwait(false);

        var responseJson = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(responseJson))
            throw new IOException("Sync host returned an empty response.");

        return JsonSerializer.Deserialize<SyncHostResponseEnvelope>(responseJson, SyncHostJson.Options)
               ?? throw new IOException("Sync host returned an invalid response.");
    }
}
