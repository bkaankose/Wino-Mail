using System.Collections.Concurrent;
using System.Text.Json;
using Wino.AppServices.Contracts;
using Wino.Core.Domain.Interfaces;

namespace Wino.Companion.Services;

public sealed class CompanionShutdownCoordinator(
    CompanionAppService appService,
    ICompanionBackendControl backendControl)
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> pendingFlushes = new();

    public void RegisterRpcHandler()
    {
        appService.Methods.Register("companion.flush-complete.v1", (request, _) =>
        {
            if (request.Payload is not null)
            {
                using var payload = JsonDocument.Parse(request.Payload);
                if (payload.RootElement.TryGetProperty("requestId", out var requestIdElement) &&
                    requestIdElement.TryGetGuid(out var requestId))
                {
                    CompleteFlush(requestId);
                }
            }

            return Task.FromResult(RpcResponse.Success());
        });
    }

    public async Task RequestExitAsync(Action terminate)
    {
        if (!appService.HasAttachedClient)
        {
            terminate();
            return;
        }

        await RequestUiFlushAsync().ConfigureAwait(false);

        using var flushCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await backendControl.FlushAsync(flushCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (flushCancellation.IsCancellationRequested)
        {
            // Exit remains bounded when a provider or queued operation cannot finish promptly.
        }
        finally
        {
            terminate();
        }
    }

    private async Task RequestUiFlushAsync()
    {
        var requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingFlushes[requestId] = completion;

        _ = appService.PublishMessageAsync(new MessengerEnvelope(
            "lifecycle.shutdown-flush-requested.v1",
            WinoAppServiceJsonContext.Serialize(new ShutdownFlushRequest(
                requestId,
                DateTimeOffset.UtcNow.AddSeconds(5).UtcTicks,
                true))));

        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Exit remains bounded when an attached UI is hung or crashed.
        }
        finally
        {
            pendingFlushes.TryRemove(requestId, out _);
        }
    }

    public void CompleteFlush(Guid requestId)
    {
        if (pendingFlushes.TryRemove(requestId, out var completion))
        {
            completion.TrySetResult();
        }
    }
}
