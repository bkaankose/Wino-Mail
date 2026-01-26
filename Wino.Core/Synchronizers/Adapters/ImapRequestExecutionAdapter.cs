using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit.Net.Imap;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;
using Wino.Core.Integration;
using Wino.Core.Services;

namespace Wino.Core.Synchronizers.Adapters;

/// <summary>
/// Adapter for IMAP synchronizer that integrates with the new RequestExecutionEngine.
/// IMAP doesn't support native batch operations, so requests are executed sequentially using a client pool.
/// </summary>
public class ImapRequestExecutionAdapter
{
    private readonly ImapClientPool _clientPool;
    private readonly RequestExecutionEngine _executionEngine;
    private readonly ILogger _logger = Log.ForContext<ImapRequestExecutionAdapter>();

    public ImapRequestExecutionAdapter(ImapClientPool clientPool, RequestExecutionEngine executionEngine)
    {
        _clientPool = clientPool;
        _executionEngine = executionEngine;
    }

    /// <summary>
    /// Executes a batch of executable requests using IMAP client pool.
    /// Since IMAP doesn't support batch operations, requests are executed sequentially.
    /// </summary>
    public async Task<List<IRequestExecutionResult>> ExecuteBatchAsync(
        IEnumerable<IExecutableRequest> requests,
        IRequestExecutionContext context)
    {
        // Prepare all requests
        var preparedRequests = await _executionEngine.PrepareRequestsAsync(requests, context).ConfigureAwait(false);

        // Execute using IMAP client
        var results = await _executionEngine.ExecuteBatchAsync(
            preparedRequests,
            (nativeRequests, ct) => ExecuteImapRequestsAsync(nativeRequests, ct),
            context).ConfigureAwait(false);

        return results;
    }

    /// <summary>
    /// Executes IMAP requests sequentially using a client from the pool.
    /// </summary>
    private async Task<Dictionary<object, BatchItemResponse>> ExecuteImapRequestsAsync(
        IEnumerable<object> nativeRequests,
        CancellationToken cancellationToken)
    {
        var responseMap = new Dictionary<object, BatchItemResponse>();
        IImapClient client = null;
        bool clientAcquired = false;

        try
        {
            // Get a client from the pool
            client = await _clientPool.GetClientAsync().ConfigureAwait(false);
            clientAcquired = true;

            // Execute each request sequentially
            foreach (var nativeRequest in nativeRequests)
            {
                if (nativeRequest is ImapTaskRequest imapTask)
                {
                    try
                    {
                        // Execute the task
                        await imapTask.ExecuteAsync(client, cancellationToken).ConfigureAwait(false);

                        // IMAP requests typically don't return meaningful responses
                        responseMap[nativeRequest] = BatchItemResponse.Success(null);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "IMAP request failed");
                        responseMap[nativeRequest] = BatchItemResponse.Failure(ex);
                    }
                }
                else
                {
                    _logger.Warning("Invalid native request type: {Type}", nativeRequest?.GetType().Name);
                    responseMap[nativeRequest] = BatchItemResponse.Failure(
                        new InvalidOperationException($"Expected ImapTaskRequest but got {nativeRequest?.GetType().Name}"));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to execute IMAP requests");

            // Mark all requests as failed
            foreach (var nativeRequest in nativeRequests)
            {
                if (!responseMap.ContainsKey(nativeRequest))
                {
                    responseMap[nativeRequest] = BatchItemResponse.Failure(ex);
                }
            }
        }
        finally
        {
            // Release the client back to the pool
            if (clientAcquired && client != null)
            {
                _clientPool.Release(client);
            }
        }

        return responseMap;
    }
}

/// <summary>
/// Wrapper for IMAP task-based requests.
/// </summary>
public class ImapTaskRequest
{
    private readonly Func<IImapClient, CancellationToken, Task> _task;

    public ImapTaskRequest(Func<IImapClient, CancellationToken, Task> task)
    {
        _task = task;
    }

    public Task ExecuteAsync(IImapClient client, CancellationToken cancellationToken)
    {
        return _task(client, cancellationToken);
    }
}

/// <summary>
/// Wrapper for IMAP task-based requests with a response.
/// </summary>
public class ImapTaskRequest<TResponse> : ImapTaskRequest
{
    private readonly Func<IImapClient, CancellationToken, Task<TResponse>> _taskWithResponse;
    private TResponse _response;

    public TResponse Response => _response;

    public ImapTaskRequest(Func<IImapClient, CancellationToken, Task<TResponse>> taskWithResponse)
        : base(async (client, ct) =>
        {
            // This is a placeholder that will be replaced by ExecuteAsync
            await Task.CompletedTask;
        })
    {
        _taskWithResponse = taskWithResponse;
    }

    public new async Task ExecuteAsync(IImapClient client, CancellationToken cancellationToken)
    {
        _response = await _taskWithResponse(client, cancellationToken).ConfigureAwait(false);
    }
}
