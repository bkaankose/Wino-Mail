using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Requests.Bundles;

public record HttpRequestBundle<TRequest>(TRequest NativeRequest, IUIChangeRequest UIChangeRequest, IRequestBase Request = null) : IExecutableRequest<TRequest>
    where TRequest : class
{
    // IRequestBase implementation
    public object GroupingKey() => Request?.GroupingKey() ?? string.Empty;
    public int ResynchronizationDelay => Request?.ResynchronizationDelay ?? 0;

    // IUIChangeRequest implementation
    public void ApplyUIChanges() => UIChangeRequest?.ApplyUIChanges();
    public void RevertUIChanges() => UIChangeRequest?.RevertUIChanges();

    // IExecutableRequest implementation
    Task<object> IExecutableRequest.PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        ApplyUIChanges();
        return Task.FromResult<object>(NativeRequest);
    }

    Task<TRequest> IExecutableRequest<TRequest>.PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        ApplyUIChanges();
        return Task.FromResult(NativeRequest);
    }

    public virtual Task HandleResponseAsync(object response, IRequestExecutionContext context)
    {
        return Task.CompletedTask;
    }

    public virtual Task HandleFailureAsync(Exception error, IRequestExecutionContext context)
    {
        RevertUIChanges();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Bundle that encapsulates batch request and native request with response.
/// </summary>
/// <typeparam name="TRequest">Http type for each integrator. eg. ClientServiceRequest for Gmail and RequestInformation for Microsoft Graph.</typeparam>
/// <typeparam name="TResponse">Response type from the server.</typeparam>
/// <param name="NativeRequest">Native type to send via http.</param>
/// <param name="Request">The request that originated this bundle.</param>
public record HttpRequestBundle<TRequest, TResponse>(TRequest NativeRequest, IRequestBase Request) : HttpRequestBundle<TRequest>(NativeRequest, Request), IExecutableRequest<TRequest, TResponse>
    where TRequest : class
    where TResponse : class
{
    public async Task<TResponse> DeserializeBundleAsync(HttpResponseMessage httpResponse, JsonTypeInfo<TResponse> typeInfo, CancellationToken cancellationToken = default)
    {
        var content = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

        return JsonSerializer.Deserialize(content, typeInfo) ?? throw new InvalidOperationException("Invalid Http Response Deserialization");
    }

    // Override to handle typed responses
    public virtual Task HandleResponseAsync(TResponse response, IRequestExecutionContext context)
    {
        return Task.CompletedTask;
    }
}
