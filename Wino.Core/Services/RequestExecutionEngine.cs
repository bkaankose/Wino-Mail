using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Services;

/// <summary>
/// Orchestrates the execution of batched requests with proper error handling, rollback, and response capture.
/// This engine supports:
/// - Async request preparation (for database queries)
/// - Per-request error handling and rollback
/// - Response capture and propagation
/// - Batch execution with configurable batch sizes
/// </summary>
public class RequestExecutionEngine
{
    private readonly ILogger _logger = Log.ForContext<RequestExecutionEngine>();

    /// <summary>
    /// Prepares a batch of requests for execution by calling their PrepareNativeRequestAsync methods.
    /// This allows requests to perform async operations like database queries before execution.
    /// </summary>
    /// <param name="requests">Requests to prepare.</param>
    /// <param name="context">Execution context.</param>
    /// <returns>Dictionary mapping requests to their prepared native request objects.</returns>
    public async Task<Dictionary<IExecutableRequest, object>> PrepareRequestsAsync(
        IEnumerable<IExecutableRequest> requests,
        IRequestExecutionContext context)
    {
        var prepared = new Dictionary<IExecutableRequest, object>();

        foreach (var request in requests)
        {
            try
            {
                var nativeRequest = await request.PrepareNativeRequestAsync(context).ConfigureAwait(false);
                prepared[request] = nativeRequest;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to prepare request {RequestType}", request.GetType().Name);
                // If preparation fails, we'll mark it as failed and continue with others
                prepared[request] = null;
            }
        }

        return prepared;
    }

    /// <summary>
    /// Executes a batch of prepared requests and processes their responses.
    /// Each request is tracked individually for success/failure.
    /// </summary>
    /// <param name="preparedRequests">Dictionary mapping requests to their native request objects.</param>
    /// <param name="batchExecutor">Function that executes the batch and returns response mapping.</param>
    /// <param name="context">Execution context.</param>
    /// <returns>List of execution results for each request.</returns>
    public async Task<List<IRequestExecutionResult>> ExecuteBatchAsync(
        Dictionary<IExecutableRequest, object> preparedRequests,
        Func<IEnumerable<object>, CancellationToken, Task<Dictionary<object, BatchItemResponse>>> batchExecutor,
        IRequestExecutionContext context)
    {
        var results = new List<IRequestExecutionResult>();

        // Filter out requests that failed preparation
        var validRequests = preparedRequests.Where(kvp => kvp.Value != null).ToList();

        if (!validRequests.Any())
        {
            _logger.Warning("No valid requests to execute in batch");
            return results;
        }

        // Apply UI changes before execution
        foreach (var kvp in validRequests)
        {
            try
            {
                if (context.Dispatcher != null)
                    await context.Dispatcher.ExecuteOnUIThread(() => kvp.Key.ApplyUIChanges());
                else
                    kvp.Key.ApplyUIChanges();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to apply UI changes for {RequestType}", kvp.Key.GetType().Name);
            }
        }

        try
        {
            // Execute the batch
            var nativeRequests = validRequests.Select(kvp => kvp.Value);
            var responses = await batchExecutor(nativeRequests, context.CancellationToken).ConfigureAwait(false);

            // Process each response
            foreach (var kvp in validRequests)
            {
                var request = kvp.Key;
                var nativeRequest = kvp.Value;

                if (responses.TryGetValue(nativeRequest, out var response))
                {
                    var result = await ProcessSingleResponseAsync(request, response, context).ConfigureAwait(false);
                    results.Add(result);
                }
                else
                {
                    // No response found - treat as failure
                    var result = await ProcessFailureAsync(
                        request,
                        new InvalidOperationException("No response received for request"),
                        context).ConfigureAwait(false);
                    results.Add(result);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Batch execution failed");

            // If the entire batch failed, mark all requests as failed
            foreach (var kvp in validRequests)
            {
                var result = await ProcessFailureAsync(kvp.Key, ex, context).ConfigureAwait(false);
                results.Add(result);
            }
        }

        // Add results for requests that failed preparation
        foreach (var kvp in preparedRequests.Where(kvp => kvp.Value == null))
        {
            results.Add(RequestExecutionResultFactory.Failure(
                kvp.Key,
                new InvalidOperationException("Request preparation failed")));
        }

        return results;
    }

    /// <summary>
    /// Processes a single response from the batch execution.
    /// </summary>
    private async Task<IRequestExecutionResult> ProcessSingleResponseAsync(
        IExecutableRequest request,
        BatchItemResponse response,
        IRequestExecutionContext context)
    {
        if (response.IsSuccess)
        {
            try
            {
                // Let the request handle its response
                await request.HandleResponseAsync(response.Content, context).ConfigureAwait(false);

                return RequestExecutionResultFactory.Success(request, response.Content);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to handle response for {RequestType}", request.GetType().Name);
                return await ProcessFailureAsync(request, ex, context).ConfigureAwait(false);
            }
        }
        else
        {
            return await ProcessFailureAsync(request, response.Error, context).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Processes a failure for a single request, including rollback.
    /// </summary>
    private async Task<IRequestExecutionResult> ProcessFailureAsync(
        IExecutableRequest request,
        Exception error,
        IRequestExecutionContext context)
    {
        _logger.Error(error, "Request {RequestType} failed", request.GetType().Name);

        try
        {
            // Revert UI changes
            if (context.Dispatcher != null)
                await context.Dispatcher.ExecuteOnUIThread(() => request.RevertUIChanges());
            else
                request.RevertUIChanges();

            // Let the request handle its failure (e.g., database rollback)
            await request.HandleFailureAsync(error, context).ConfigureAwait(false);
        }
        catch (Exception rollbackEx)
        {
            _logger.Error(rollbackEx, "Failed to rollback request {RequestType}", request.GetType().Name);
        }

        return RequestExecutionResultFactory.Failure(request, error);
    }
}

/// <summary>
/// Represents a response from a single item in a batch execution.
/// </summary>
public class BatchItemResponse
{
    public bool IsSuccess { get; set; }
    public object Content { get; set; }
    public Exception Error { get; set; }

    public static BatchItemResponse Success(object content) => new()
    {
        IsSuccess = true,
        Content = content
    };

    public static BatchItemResponse Failure(Exception error) => new()
    {
        IsSuccess = false,
        Error = error
    };
}
