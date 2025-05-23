﻿using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Requests.Bundles;

public record HttpRequestBundle<TRequest>(TRequest NativeRequest, IUIChangeRequest UIChangeRequest, IRequestBase Request = null) : IRequestBundle<TRequest>
{
    public string BundleId { get; set; } = string.Empty;
}

/// <summary>
/// Bundle that encapsulates batch request and native request with response.
/// </summary>
/// <typeparam name="TRequest">Http type for each integrator. eg. ClientServiceRequest for Gmail and RequestInformation for Microsoft Graph.</typeparam>
/// <param name="NativeRequest">Native type to send via http.</param>
/// <param name="BatchRequest">Batch request that is generated by base synchronizer.</param>
public record HttpRequestBundle<TRequest, TResponse>(TRequest NativeRequest, IRequestBase Request) : HttpRequestBundle<TRequest>(NativeRequest, Request)
{
    [RequiresDynamicCode("AOT")]
    [RequiresUnreferencedCode("AOT")]
    public async Task<TResponse> DeserializeBundleAsync(HttpResponseMessage httpResponse, CancellationToken cancellationToken = default)
    {
        var content = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

        return JsonSerializer.Deserialize<TResponse>(content) ?? throw new InvalidOperationException("Invalid Http Response Deserialization");
    }
}
