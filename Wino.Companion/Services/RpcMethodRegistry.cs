using System.Collections.Concurrent;
using System.Text.Json;
using Wino.AppServices.Contracts;
using Wino.Core.Domain.Exceptions;

namespace Wino.Companion.Services;

public sealed class RpcMethodRegistry
{
    private static readonly TimeSpan BackendReadinessTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Func<RpcRequest, CancellationToken, Task<RpcResponse>>> handlers =
        new(StringComparer.Ordinal);
    private IWinoRpcRequestHandler? generatedDispatcher;
    private Func<CancellationToken, Task>? generatedDispatcherReadiness;

    public void Register(string methodId, Func<RpcRequest, CancellationToken, Task<RpcResponse>> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodId);
        ArgumentNullException.ThrowIfNull(handler);

        if (!handlers.TryAdd(methodId, handler))
        {
            throw new InvalidOperationException($"An RPC handler is already registered for '{methodId}'.");
        }
    }

    public void RegisterGeneratedDispatcher(
        IWinoRpcRequestHandler dispatcher,
        Func<CancellationToken, Task>? readiness = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (Interlocked.CompareExchange(ref generatedDispatcher, dispatcher, null) is not null)
        {
            throw new InvalidOperationException("The generated RPC dispatcher is already registered.");
        }

        generatedDispatcherReadiness = readiness;
    }

    public Task<RpcResponse> InvokeAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        if (handlers.TryGetValue(request.MethodId, out var handler))
        {
            return handler(request, cancellationToken);
        }

        if (generatedDispatcher is not null)
        {
            return InvokeGeneratedAsync(generatedDispatcher, request, cancellationToken);
        }

        return Task.FromResult(RpcResponse.Failure(
            RpcErrorCode.UnsupportedMethod,
            $"No companion handler is registered for '{request.MethodId}'."));
    }

    private async Task<RpcResponse> InvokeGeneratedAsync(
        IWinoRpcRequestHandler dispatcher,
        RpcRequest request,
        CancellationToken cancellationToken)
    {
        // The AppService connection is opened before the backend finishes starting so
        // that a freshly launched UI never sees a missing companion. Requests arriving
        // in that window wait for readiness instead of hitting uninitialized services.
        if (generatedDispatcherReadiness is { } readiness)
        {
            try
            {
                await readiness(cancellationToken).WaitAsync(BackendReadinessTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return RpcResponse.Failure(
                    RpcErrorCode.CompanionUnavailable,
                    "The companion backend is still starting.",
                    RequiredUserAction.Retry);
            }
        }

        try
        {
            using var document = JsonDocument.Parse(request.Payload ?? "{}");
            var response = await dispatcher
                .HandleRequestAsync(request.MethodId, document.RootElement, cancellationToken)
                .ConfigureAwait(false);
            return RpcResponse.Success(response is null ? null : System.Text.Encoding.UTF8.GetString(response));
        }
        catch (InvalidOperationException exception) when (exception.Message.StartsWith("No dispatcher registered", StringComparison.Ordinal) || exception.Message.StartsWith("Unknown RPC method", StringComparison.Ordinal))
        {
            return RpcResponse.Failure(RpcErrorCode.UnsupportedMethod, exception.Message);
        }
        catch (AuthenticationAttentionException)
        {
            return RpcResponse.Failure(
                RpcErrorCode.InteractiveWindowUnavailable,
                "Interactive authentication requires an attached Wino window.",
                RequiredUserAction.Reauthenticate);
        }
        catch (AccountSetupCanceledException)
        {
            // Keep the stable exception name in the message while the shared ViewModel
            // transition still supports both in-process and remote backends.
            return RpcResponse.Failure(
                RpcErrorCode.OperationCanceled,
                nameof(AccountSetupCanceledException));
        }
    }
}
