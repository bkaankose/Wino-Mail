using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions;
using MoreLinq;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Services;

namespace Wino.Core.Synchronizers.Adapters;

/// <summary>
/// Error response model for Microsoft Graph API - AOT-compatible.
/// </summary>
public class GraphErrorResponse
{
    public GraphError Error { get; set; }
}

public class GraphError
{
    public string Code { get; set; }
    public string Message { get; set; }
}

/// <summary>
/// JSON serialization context for Outlook adapter - enables Native AOT compilation.
/// </summary>
[JsonSerializable(typeof(GraphErrorResponse))]
[JsonSerializable(typeof(GraphError))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class OutlookAdapterJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Adapter for Outlook synchronizer that integrates with the new RequestExecutionEngine.
/// Handles batch execution using Microsoft Graph BatchRequestContentCollection.
/// Leverages Graph SDK's built-in retry and rate limiting handlers (RetryHandler, RedirectHandler).
/// Circuit breaker provides additional local protection against cascade failures.
/// AOT-compatible with source-generated JSON serialization.
/// </summary>
public class OutlookRequestExecutionAdapter
{
    private readonly GraphServiceClient _graphClient;
    private readonly RequestExecutionEngine _executionEngine;
    private readonly ILogger _logger = Log.ForContext<OutlookRequestExecutionAdapter>();
    private readonly OutlookAdapterJsonContext _jsonContext = OutlookAdapterJsonContext.Default;
    private const int MaxBatchSize = 20;
    private int _consecutiveFailures = 0;
    private DateTime _lastFailureTime = DateTime.MinValue;
    private const int CircuitBreakerThreshold = 5;
    private const int CircuitBreakerResetMinutes = 5;

    public OutlookRequestExecutionAdapter(GraphServiceClient graphClient, RequestExecutionEngine executionEngine)
    {
        _graphClient = graphClient;
        _executionEngine = executionEngine;
    }

    /// <summary>
    /// Executes a batch of executable requests using Microsoft Graph batch API.
    /// Retry and rate limiting are handled transparently by Graph SDK middleware (RetryHandler, GraphRateLimitHandler).
    /// Circuit breaker provides additional protection against repeated failures.
    /// </summary>
    public async Task<List<IRequestExecutionResult>> ExecuteBatchAsync(
        IEnumerable<IExecutableRequest> requests,
        IRequestExecutionContext context)
    {
        // Check circuit breaker
        if (IsCircuitBreakerOpen())
        {
            _logger.Warning("Circuit breaker is open. Skipping batch execution. Consecutive failures: {Failures}", _consecutiveFailures);
            return requests.Select(r => RequestExecutionResultFactory.Failure(
                r as IRequestBase,
                new InvalidOperationException("Circuit breaker is open due to consecutive failures. Please try again later."))).ToList();
        }

        var allResults = new List<IRequestExecutionResult>();

        // Split into batches of MaxBatchSize
        var batches = requests.Batch(MaxBatchSize);

        foreach (var batch in batches)
        {
            var batchResults = await ExecuteSingleBatchAsync(batch, context).ConfigureAwait(false);
            allResults.AddRange(batchResults);
        }

        return allResults;
    }

    /// <summary>
    /// Checks if the circuit breaker is open.
    /// </summary>
    private bool IsCircuitBreakerOpen()
    {
        if (_consecutiveFailures < CircuitBreakerThreshold)
            return false;

        var minutesSinceLastFailure = (DateTime.UtcNow - _lastFailureTime).TotalMinutes;
        if (minutesSinceLastFailure >= CircuitBreakerResetMinutes)
        {
            _logger.Information("Circuit breaker reset after {Minutes} minutes", minutesSinceLastFailure);
            _consecutiveFailures = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Executes a single batch. Graph SDK handlers handle retries and rate limiting transparently.
    /// </summary>
    private async Task<List<IRequestExecutionResult>> ExecuteSingleBatchAsync(
        IEnumerable<IExecutableRequest> requests,
        IRequestExecutionContext context)
    {
        try
        {
            // Prepare all requests
            var preparedRequests = await _executionEngine.PrepareRequestsAsync(requests, context).ConfigureAwait(false);

            // Execute using Graph batch API - SDK middleware handles retries
            var results = await _executionEngine.ExecuteBatchAsync(
                preparedRequests,
                (nativeRequests, ct) => ExecuteGraphBatchAsync(nativeRequests, ct),
                context).ConfigureAwait(false);

            // Success - reset circuit breaker
            _consecutiveFailures = 0;
            
            return results;
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("Batch execution cancelled");
            throw; // Don't count cancellation as failure
        }
        catch (Exception ex)
        {
            // Increment consecutive failures for circuit breaker
            _consecutiveFailures++;
            _lastFailureTime = DateTime.UtcNow;
            
            _logger.Error(ex, "Batch execution failed. Consecutive failures: {Failures}", _consecutiveFailures);
            throw;
        }
    }

    /// <summary>
    /// Executes a batch of native RequestInformation objects using Microsoft Graph Batch API.
    /// </summary>
    private async Task<Dictionary<object, BatchItemResponse>> ExecuteGraphBatchAsync(
        IEnumerable<object> nativeRequests,
        CancellationToken cancellationToken)
    {
        var responseMap = new Dictionary<object, BatchItemResponse>();
        var requestMap = new Dictionary<string, object>(); // Maps batch request ID to native request

        var batchContent = new BatchRequestContentCollection(_graphClient);

        // Add all requests to batch
        foreach (var nativeRequest in nativeRequests)
        {
            if (nativeRequest is RequestInformation requestInfo)
            {
                try
                {
                    var batchRequestId = await batchContent.AddBatchRequestStepAsync(requestInfo).ConfigureAwait(false);
                    requestMap[batchRequestId] = nativeRequest;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to add request to batch");
                    responseMap[nativeRequest] = BatchItemResponse.Failure(ex);
                }
            }
            else
            {
                _logger.Warning("Invalid native request type: {Type}", nativeRequest?.GetType().Name);
                responseMap[nativeRequest] = BatchItemResponse.Failure(
                    new InvalidOperationException($"Expected RequestInformation but got {nativeRequest?.GetType().Name}"));
            }
        }

        // Check if we need serial execution (for SendDraft requests)
        ConfigureSerialExecutionIfNeeded(batchContent, nativeRequests);

        // Execute batch
        try
        {
            var batchResponse = await _graphClient.Batch.PostAsync(batchContent, cancellationToken).ConfigureAwait(false);

            // Process each response
            foreach (var batchRequestId in requestMap.Keys)
            {
                var nativeRequest = requestMap[batchRequestId];
                var httpResponse = await batchResponse.GetResponseByIdAsync(batchRequestId).ConfigureAwait(false);

                if (httpResponse == null)
                {
                    responseMap[nativeRequest] = BatchItemResponse.Failure(
                        new InvalidOperationException("No response received from batch"));
                    continue;
                }

                using (httpResponse)
                {
                    if (httpResponse.IsSuccessStatusCode)
                    {
                        // Try to read response content
                        object content = null;
                        try
                        {
                            var contentString = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(contentString))
                            {
                                // Store as string for downstream processing
                                // Individual requests can deserialize with their own type info
                                content = contentString;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Warning(ex, "Failed to read response content");
                        }

                        responseMap[nativeRequest] = BatchItemResponse.Success(content);
                    }
                    else
                    {
                        var error = await ParseErrorResponseAsync(httpResponse).ConfigureAwait(false);
                        responseMap[nativeRequest] = BatchItemResponse.Failure(error);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Batch execution failed");

            // Mark all requests as failed
            foreach (var nativeRequest in requestMap.Values)
            {
                if (!responseMap.ContainsKey(nativeRequest))
                {
                    responseMap[nativeRequest] = BatchItemResponse.Failure(ex);
                }
            }
        }

        return responseMap;
    }

    /// <summary>
    /// Configures serial execution if the batch contains SendDraft requests.
    /// </summary>
    private void ConfigureSerialExecutionIfNeeded(
        BatchRequestContentCollection batchContent,
        IEnumerable<object> nativeRequests)
    {
        // Check if any request is a SendDraft (these need serial execution)
        bool requiresSerial = nativeRequests.Any(req =>
        {
            if (req is RequestInformation requestInfo)
            {
                // Check if this is a send draft request (POST to /messages/{id}/send)
                return requestInfo.HttpMethod == Microsoft.Kiota.Abstractions.Method.POST &&
                       requestInfo.URI?.ToString().Contains("/send") == true;
            }
            return false;
        });

        if (requiresSerial)
        {
            var steps = batchContent.BatchRequestSteps.ToList();
            for (int i = 1; i < steps.Count; i++)
            {
                var currentStep = steps[i].Value;
                var previousStepKey = steps[i - 1].Key;
                currentStep.DependsOn = [previousStepKey];
            }
        }
    }

    /// <summary>
    /// Parses error response from HTTP response message using AOT-compatible deserialization.
    /// Extracts detailed error information and handles rate limiting.
    /// </summary>
    private async Task<Exception> ParseErrorResponseAsync(HttpResponseMessage response)
    {
        try
        {
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            
            // Use source-generated JSON deserialization for AOT compatibility
            var errorResponse = JsonSerializer.Deserialize(content, _jsonContext.GraphErrorResponse);
            
            var errorCode = errorResponse?.Error?.Code ?? "Unknown";
            var errorMessage = errorResponse?.Error?.Message ?? response.ReasonPhrase;
            var statusCode = (int)response.StatusCode;

            // Check for rate limiting
            if (statusCode == 429)
            {
                if (response.Headers.TryGetValues("Retry-After", out var retryAfterValues))
                {
                    var retryAfter = retryAfterValues.FirstOrDefault();
                    _logger.Warning("Rate limited. Retry-After: {RetryAfter}", retryAfter);
                    errorMessage = $"{errorMessage} (Retry-After: {retryAfter})";
                }
            }

            // Log detailed error information
            _logger.Error("Graph API error: Status={StatusCode}, Code={ErrorCode}, Message={ErrorMessage}", 
                statusCode, errorCode, errorMessage);

            // Create appropriate exception type based on error
            return statusCode switch
            {
                401 => new UnauthorizedAccessException($"[{statusCode}] {errorCode}: {errorMessage}"),
                403 => new UnauthorizedAccessException($"[{statusCode}] {errorCode}: {errorMessage}"),
                404 => new InvalidOperationException($"[{statusCode}] {errorCode}: Entity not found - {errorMessage}"),
                429 => new HttpRequestException($"[{statusCode}] Rate limit exceeded: {errorMessage}"),
                >= 500 => new HttpRequestException($"[{statusCode}] Server error: {errorMessage}"),
                _ => new HttpRequestException($"[{statusCode}] {errorCode}: {errorMessage}")
            };
        }
        catch (JsonException jsonEx)
        {
            _logger.Error(jsonEx, "Failed to parse error response as JSON");
            return new HttpRequestException($"Request failed with status {response.StatusCode}. Unable to parse error details.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to parse error response");
            return new HttpRequestException($"Request failed with status {response.StatusCode}");
        }
    }
}
