using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Wino.AppServices.Contracts;

/// <summary>
/// Transport-neutral surface consumed by generated UI proxies. The concrete UWP
/// implementation wraps every call in the reliable AppService envelope.
/// </summary>
public interface IWinoRpcClient
{
    Task<TResponse> InvokeAsync<TRequest, TResponse>(
        string methodId,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken);

    Task InvokeAsync<TRequest>(
        string methodId,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        CancellationToken cancellationToken);
}

/// <summary>Companion-side target produced for every remoted service interface.</summary>
public interface IWinoRpcRequestHandler
{
    Task<byte[]?> HandleRequestAsync(
        string methodName,
        JsonElement payload,
        CancellationToken cancellationToken);
}
