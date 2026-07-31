using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Retry;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Executes operations with automatic retry and error handling support.
/// </summary>
public interface IRetryExecutor
{
    /// <summary>
    /// Executes an operation with automatic retry based on the specified policy.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="policy">The retry policy to apply.</param>
    /// <param name="errorContextFactory">Factory to create error context from exceptions.</param>
    /// <param name="errorHandler">Optional error handler for custom error processing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    /// <exception cref="Exception">Thrown when all retries are exhausted or a fatal error occurs.</exception>
    Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryPolicy policy,
        Func<Exception, SynchronizerErrorContext> errorContextFactory,
        ISynchronizerErrorHandlerFactory errorHandler = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an operation with automatic retry based on the specified policy (void return).
    /// </summary>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="policy">The retry policy to apply.</param>
    /// <param name="errorContextFactory">Factory to create error context from exceptions.</param>
    /// <param name="errorHandler">Optional error handler for custom error processing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="Exception">Thrown when all retries are exhausted or a fatal error occurs.</exception>
    Task ExecuteWithRetryAsync(
        Func<CancellationToken, Task> operation,
        RetryPolicy policy,
        Func<Exception, SynchronizerErrorContext> errorContextFactory,
        ISynchronizerErrorHandlerFactory errorHandler = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an operation with default retry policy.
    /// </summary>
    /// <typeparam name="T">The return type of the operation.</typeparam>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="errorContextFactory">Factory to create error context from exceptions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<Exception, SynchronizerErrorContext> errorContextFactory,
        CancellationToken cancellationToken = default);
}
