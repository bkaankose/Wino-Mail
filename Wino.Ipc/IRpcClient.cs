using System;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Ipc;

/// <summary>
/// Strongly typed RPC invocation surface used by the generated remote proxies.
/// </summary>
public interface IRpcClient
{
    /// <summary>
    /// Invokes a remote method that returns a response record.
    /// </summary>
    /// <param name="methodName">Stable method identifier, e.g. "IMailService.GetMailsAsync".</param>
    /// <param name="operationId">Optional client generated id for write dedupe across reconnects.</param>
    Task<TResponse> InvokeAsync<TRequest, TResponse>(string methodName,
                                                     TRequest request,
                                                     JsonTypeInfo<TRequest> requestTypeInfo,
                                                     JsonTypeInfo<TResponse> responseTypeInfo,
                                                     Guid? operationId = null,
                                                     CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes a remote method with no return value.
    /// </summary>
    Task InvokeAsync<TRequest>(string methodName,
                               TRequest request,
                               JsonTypeInfo<TRequest> requestTypeInfo,
                               Guid? operationId = null,
                               CancellationToken cancellationToken = default);

    /// <summary>
    /// Raised for every event frame pushed by the server. Payload is the parsed event envelope.
    /// </summary>
    event Action<string, JsonElement>? EventReceived;
}
