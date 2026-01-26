using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Gmail.v1;
using Google.Apis.Requests;
using Google;
using MoreLinq;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Services;

namespace Wino.Core.Synchronizers.Adapters;

/// <summary>
/// Retry policy configuration for Gmail API requests.
/// </summary>
public class GmailRetryPolicy
{
    public int MaxRetries { get; set; } = 3;
    public int InitialDelayMs { get; set; } = 1000;
    public int MaxDelayMs { get; set; } = 30000;
    public double BackoffMultiplier { get; set; } = 2.0;
}

/// <summary>
/// Adapter for Gmail synchronizer that integrates with the new RequestExecutionEngine.
/// Handles batch execution using Gmail BatchRequest API with retry logic and error handling.
/// </summary>
public class GmailRequestExecutionAdapter
{
    private readonly GmailService _gmailService;
    private readonly RequestExecutionEngine _executionEngine;
    private readonly ILogger _logger = Log.ForContext<GmailRequestExecutionAdapter>();
    private readonly GmailRetryPolicy _retryPolicy;
    private const int MaxBatchSize = 10; // Gmail SDK has known memory issues with larger batches
    private int _consecutiveFailures = 0;
    private DateTime _lastFailureTime = DateTime.MinValue;
    private const int CircuitBreakerThreshold = 5;
    private const int CircuitBreakerResetMinutes = 5;

    public GmailRequestExecutionAdapter(GmailService gmailService, RequestExecutionEngine executionEngine, GmailRetryPolicy retryPolicy = null)
    {
        _gmailService = gmailService;
        _executionEngine = executionEngine;
        _retryPolicy = retryPolicy ?? new GmailRetryPolicy();
    }

    /// <summary>
    /// Executes a batch of executable requests using Gmail batch API with retry logic.
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
            var batchResults = await ExecuteSingleBatchWithRetryAsync(batch, context).ConfigureAwait(false);
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
    /// Executes a single batch with retry logic and exponential backoff.
    /// </summary>
    private async Task<List<IRequestExecutionResult>> ExecuteSingleBatchWithRetryAsync(
        IEnumerable<IExecutableRequest> requests,
        IRequestExecutionContext context)
    {
        var attempt = 0;
        var delay = _retryPolicy.InitialDelayMs;
        Exception lastException = null;

        while (attempt < _retryPolicy.MaxRetries)
        {
            try
            {
                var results = await ExecuteSingleBatchAsync(requests, context).ConfigureAwait(false);
                
                // Success - reset circuit breaker
                _consecutiveFailures = 0;
                
                return results;
            }
            catch (GoogleApiException googleEx) when (IsRetryableGoogleError(googleEx))
            {
                lastException = googleEx;
                attempt++;
                
                if (attempt < _retryPolicy.MaxRetries)
                {
                    var actualDelay = Math.Min(delay, _retryPolicy.MaxDelayMs);
                    _logger.Warning(googleEx, "Retryable Gmail error on attempt {Attempt}/{MaxRetries}. Error code: {ErrorCode}. Retrying in {Delay}ms", 
                        attempt, _retryPolicy.MaxRetries, googleEx.HttpStatusCode, actualDelay);
                    
                    await Task.Delay(actualDelay, context.CancellationToken).ConfigureAwait(false);
                    delay = (int)(delay * _retryPolicy.BackoffMultiplier);
                }
            }
            catch (TaskCanceledException)
            {
                _logger.Warning("Request timeout or cancellation");
                throw; // Don't retry cancellations
            }
            catch (OutOfMemoryException memEx)
            {
                // Known Gmail SDK issue - treat as non-retryable
                _logger.Error(memEx, "Gmail SDK OutOfMemoryException - known SDK bug");
                _consecutiveFailures++;
                _lastFailureTime = DateTime.UtcNow;
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Non-retryable error during batch execution");
                _consecutiveFailures++;
                _lastFailureTime = DateTime.UtcNow;
                throw;
            }
        }

        // All retries exhausted
        _consecutiveFailures++;
        _lastFailureTime = DateTime.UtcNow;
        _logger.Error(lastException, "All {MaxRetries} retry attempts exhausted", _retryPolicy.MaxRetries);
        throw lastException ?? new InvalidOperationException("Batch execution failed after all retries");
    }

    /// <summary>
    /// Determines if a Google API error is retryable.
    /// </summary>
    private bool IsRetryableGoogleError(GoogleApiException ex)
    {
        if (ex.HttpStatusCode == System.Net.HttpStatusCode.Unused)
            return false;

        var statusCode = (int)ex.HttpStatusCode;
        
        // Retry on rate limiting (429), server errors (5xx), and specific transient errors
        return statusCode == 429 ||      // Rate limiting
               statusCode == 503 ||      // Service unavailable
               statusCode == 504 ||      // Gateway timeout
               statusCode == 500 ||      // Internal server error
               statusCode == 502;        // Bad gateway
    }

    private async Task<List<IRequestExecutionResult>> ExecuteSingleBatchAsync(
        IEnumerable<IExecutableRequest> requests,
        IRequestExecutionContext context)
    {
        // Prepare all requests
        var preparedRequests = await _executionEngine.PrepareRequestsAsync(requests, context).ConfigureAwait(false);

        // Execute using Gmail batch API
        var results = await _executionEngine.ExecuteBatchAsync(
            preparedRequests,
            (nativeRequests, ct) => ExecuteGmailBatchAsync(nativeRequests, ct),
            context).ConfigureAwait(false);

        return results;
    }

    /// <summary>
    /// Executes a batch of native IClientServiceRequest objects using Gmail Batch API.
    /// </summary>
    private async Task<Dictionary<object, BatchItemResponse>> ExecuteGmailBatchAsync(
        IEnumerable<object> nativeRequests,
        CancellationToken cancellationToken)
    {
        var responseMap = new Dictionary<object, BatchItemResponse>();
        var requestTasks = new Dictionary<object, TaskCompletionSource<BatchItemResponse>>();

        var batchRequest = new BatchRequest(_gmailService);

        // Queue all requests
        foreach (var nativeRequest in nativeRequests)
        {
            if (nativeRequest is IClientServiceRequest clientRequest)
            {
                try
                {
                    var tcs = new TaskCompletionSource<BatchItemResponse>();
                    requestTasks[nativeRequest] = tcs;

                    batchRequest.Queue<object>(clientRequest, (content, error, index, message) =>
                    {
                        if (error != null)
                        {
                            var exception = CreateExceptionFromRequestError(error);
                            tcs.SetResult(BatchItemResponse.Failure(exception));
                        }
                        else
                        {
                            tcs.SetResult(BatchItemResponse.Success(content));
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to queue request to batch");
                    responseMap[nativeRequest] = BatchItemResponse.Failure(ex);
                }
            }
            else
            {
                _logger.Warning("Invalid native request type: {Type}", nativeRequest?.GetType().Name);
                responseMap[nativeRequest] = BatchItemResponse.Failure(
                    new InvalidOperationException($"Expected IClientServiceRequest but got {nativeRequest?.GetType().Name}"));
            }
        }

        // Execute batch
        try
        {
            await batchRequest.ExecuteAsync(cancellationToken).ConfigureAwait(false);

            // Wait for all responses
            foreach (var kvp in requestTasks)
            {
                var response = await kvp.Value.Task.ConfigureAwait(false);
                responseMap[kvp.Key] = response;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Batch execution failed");

            // Mark all requests that haven't completed as failed
            foreach (var kvp in requestTasks)
            {
                if (!responseMap.ContainsKey(kvp.Key))
                {
                    responseMap[kvp.Key] = BatchItemResponse.Failure(ex);
                }
            }
        }

        return responseMap;
    }

    /// <summary>
    /// Creates an exception from Gmail RequestError with detailed error information.
    /// </summary>
    private Exception CreateExceptionFromRequestError(RequestError error)
    {
        if (error == null)
        {
            _logger.Warning("Received null RequestError");
            return new InvalidOperationException("Unknown error occurred");
        }

        var message = error.Message ?? "Gmail API request failed";
        var errorCode = error.Code;
        
        // Log detailed error information
        if (error.Errors != null && error.Errors.Any())
        {
            var errorDetails = string.Join(", ", error.Errors.Select(e => $"{e.Reason}: {e.Message}"));
            _logger.Error("Gmail API error: Code={ErrorCode}, Message={Message}, Details={Details}", 
                errorCode, message, errorDetails);
        }
        else
        {
            _logger.Error("Gmail API error: Code={ErrorCode}, Message={Message}", errorCode, message);
        }
        
        // Create appropriate exception type based on error code
        return errorCode switch
        {
            0 => new OutOfMemoryException($"Gmail SDK bug: {message}"),
            401 => new UnauthorizedAccessException($"[{errorCode}] Unauthorized: {message}"),
            403 => new UnauthorizedAccessException($"[{errorCode}] Forbidden: {message}"),
            404 => new InvalidOperationException($"[{errorCode}] Entity not found: {message}"),
            429 => new InvalidOperationException($"[{errorCode}] Rate limit exceeded: {message}"),
            >= 500 => new InvalidOperationException($"[{errorCode}] Server error: {message}"),
            _ => new InvalidOperationException($"[{errorCode}] {message}")
        };
    }
}
