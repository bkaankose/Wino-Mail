using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Requests;

/// <summary>
/// Default implementation of request execution context.
/// </summary>
public record RequestExecutionContext(
    Guid AccountId,
    CancellationToken CancellationToken,
    IServiceProvider Services,
    IDispatcher Dispatcher = null) : Wino.Core.Domain.Interfaces.IRequestExecutionContext;

/// <summary>
/// Default implementation of request execution result.
/// </summary>
public record RequestExecutionResult(
    bool IsSuccess,
    Exception Error,
    object Response,
    Wino.Core.Domain.Interfaces.IRequestBase Request) : Wino.Core.Domain.Interfaces.IRequestExecutionResult;

/// <summary>
/// Typed implementation of request execution result.
/// Generic type is constrained to class for AOT compatibility.
/// </summary>
public record RequestExecutionResult<TResponse>(
    bool IsSuccess,
    Exception Error,
    TResponse Response,
    Wino.Core.Domain.Interfaces.IRequestBase Request) : Wino.Core.Domain.Interfaces.IRequestExecutionResult<TResponse>
    where TResponse : class
{
    object Wino.Core.Domain.Interfaces.IRequestExecutionResult.Response => Response;
}

/// <summary>
/// Factory methods for creating request execution results.
/// </summary>
public static class RequestExecutionResultFactory
{
    public static Wino.Core.Domain.Interfaces.IRequestExecutionResult Success(
        Wino.Core.Domain.Interfaces.IRequestBase request,
        object response = null)
        => new RequestExecutionResult(true, null, response, request);

    public static Wino.Core.Domain.Interfaces.IRequestExecutionResult<TResponse> Success<TResponse>(
        Wino.Core.Domain.Interfaces.IRequestBase request,
        TResponse response)
        where TResponse : class
        => new RequestExecutionResult<TResponse>(true, null, response, request);

    public static Wino.Core.Domain.Interfaces.IRequestExecutionResult Failure(
        Wino.Core.Domain.Interfaces.IRequestBase request,
        Exception error)
        => new RequestExecutionResult(false, error, null, request);

    public static Wino.Core.Domain.Interfaces.IRequestExecutionResult<TResponse> Failure<TResponse>(
        Wino.Core.Domain.Interfaces.IRequestBase request,
        Exception error)
        where TResponse : class
        => new RequestExecutionResult<TResponse>(false, error, default, request);
}
