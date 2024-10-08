﻿using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Requests
{
    /// <summary>
    /// Bundle that encapsulates batch request and native request without a response.
    /// </summary>
    /// <typeparam name="TRequest">Http type for each integrator. eg. ClientServiceRequest for Gmail and RequestInformation for Microsoft Graph.</typeparam>
    /// <param name="NativeRequest">Native type to send via http.</param>
    /// <param name="BatchRequest">Batch request that is generated by base synchronizer.</param>
    public record HttpRequestBundle<TRequest>(TRequest NativeRequest, IRequestBase Request) : IRequestBundle<TRequest>
    {
        public string BundleId { get; set; } = string.Empty;

        public override string ToString()
        {
            if (Request is IRequest singleRequest)
                return $"Single {singleRequest.Operation}. No response.";
            else if (Request is IBatchChangeRequest batchChangeRequest)
                return $"Batch {batchChangeRequest.Operation} for {batchChangeRequest.Items.Count()} items. No response.";
            else
                return "Unknown http request bundle.";
        }
    }

    /// <summary>
    /// Bundle that encapsulates batch request and native request with response.
    /// </summary>
    /// <typeparam name="TRequest">Http type for each integrator. eg. ClientServiceRequest for Gmail and RequestInformation for Microsoft Graph.</typeparam>
    /// <param name="NativeRequest">Native type to send via http.</param>
    /// <param name="BatchRequest">Batch request that is generated by base synchronizer.</param>
    public record HttpRequestBundle<TRequest, TResponse>(TRequest NativeRequest, IRequestBase Request) : HttpRequestBundle<TRequest>(NativeRequest, Request)
    {
        public async Task<TResponse> DeserializeBundleAsync(HttpResponseMessage httpResponse, CancellationToken cancellationToken = default)
        {
            var content = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

            return JsonSerializer.Deserialize<TResponse>(content) ?? throw new InvalidOperationException("Invalid Http Response Deserialization");
        }

        public override string ToString()
        {
            if (Request is IRequest singleRequest)
                return $"Single {singleRequest.Operation}. Expecting '{typeof(TResponse).FullName}' type.";
            else if (Request is IBatchChangeRequest batchChangeRequest)
                return $"Batch {batchChangeRequest.Operation} for {batchChangeRequest.Items.Count()} items. Expecting '{typeof(TResponse).FullName}' type.";
            else
                return "Unknown http request bundle.";
        }
    }
}
