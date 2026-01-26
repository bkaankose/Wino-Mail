using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Represents the execution context for a request, providing access to services and state needed during execution.
/// </summary>
public interface IRequestExecutionContext
{
    /// <summary>
    /// Cancellation token for the operation.
    /// </summary>
    CancellationToken CancellationToken { get; }

    /// <summary>
    /// Account ID for which the request is being executed.
    /// </summary>
    Guid AccountId { get; }

    /// <summary>
    /// Additional context data that can be provided by the synchronizer or execution engine.
    /// </summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Dispatcher for executing operations on the UI thread.
    /// </summary>
    IDispatcher Dispatcher { get; }
}

/// <summary>
/// Represents the result of a request execution.
/// </summary>
public interface IRequestExecutionResult
{
    /// <summary>
    /// Whether the request executed successfully.
    /// </summary>
    bool IsSuccess { get; }

    /// <summary>
    /// Error that occurred during execution, if any.
    /// </summary>
    Exception Error { get; }

    /// <summary>
    /// Optional response data from the server (e.g., created message after sending draft).
    /// </summary>
    object Response { get; }

    /// <summary>
    /// Original request that was executed.
    /// </summary>
    IRequestBase Request { get; }
}

/// <summary>
/// Represents the result of a request execution with a typed response.
/// Generic type is constrained to class for AOT compatibility.
/// </summary>
public interface IRequestExecutionResult<TResponse> : IRequestExecutionResult
    where TResponse : class
{
    /// <summary>
    /// Typed response data from the server.
    /// </summary>
    new TResponse Response { get; }
}

/// <summary>
/// Defines an executable request that can prepare its native API call, execute it, and handle responses.
/// This interface supports async preparation of requests (e.g., fetching database records).
/// </summary>
public interface IExecutableRequest : IRequestBase
{
    /// <summary>
    /// Prepares the native request (e.g., creates HttpRequestInformation for Outlook, IClientServiceRequest for Gmail).
    /// This method is called before batching and allows async operations like database queries.
    /// </summary>
    /// <param name="context">Execution context providing access to services and state.</param>
    /// <returns>Task that returns the native request object.</returns>
    Task<object> PrepareNativeRequestAsync(IRequestExecutionContext context);

    /// <summary>
    /// Called after successful execution to handle any response data from the server.
    /// For example, SendDraftRequest can capture the created message to avoid re-syncing folders.
    /// </summary>
    /// <param name="response">Response object from the server.</param>
    /// <param name="context">Execution context.</param>
    Task HandleResponseAsync(object response, IRequestExecutionContext context);

    /// <summary>
    /// Called when the request execution fails to perform any necessary rollback.
    /// This includes reverting UI changes and potentially rolling back database changes.
    /// </summary>
    /// <param name="error">The error that occurred.</param>
    /// <param name="context">Execution context.</param>
    Task HandleFailureAsync(Exception error, IRequestExecutionContext context);
}

/// <summary>
/// Defines an executable request with a typed native request.
/// Generic type is constrained to class for AOT compatibility.
/// </summary>
public interface IExecutableRequest<TNativeRequest> : IExecutableRequest
    where TNativeRequest : class
{
    /// <summary>
    /// Prepares the typed native request.
    /// </summary>
    new Task<TNativeRequest> PrepareNativeRequestAsync(IRequestExecutionContext context);
}

/// <summary>
/// Defines an executable request with both typed native request and response.
/// Generic types are constrained to class for AOT compatibility.
/// </summary>
public interface IExecutableRequest<TNativeRequest, TResponse> : IExecutableRequest<TNativeRequest>
    where TNativeRequest : class
    where TResponse : class
{
    /// <summary>
    /// Handles the typed response from the server.
    /// </summary>
    Task HandleResponseAsync(TResponse response, IRequestExecutionContext context);
}
