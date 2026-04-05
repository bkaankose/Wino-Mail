using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Retry;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Services;

/// <summary>
/// Executes operations with automatic retry and error handling support.
/// Implements exponential backoff with jitter.
/// </summary>
public class RetryExecutor : IRetryExecutor
{
    private readonly ILogger _logger = Log.ForContext<RetryExecutor>();

    /// <inheritdoc/>
    public async Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        RetryPolicy policy,
        Func<Exception, SynchronizerErrorContext> errorContextFactory,
        ISynchronizerErrorHandlerFactory errorHandler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(errorContextFactory);

        int attempt = 0;
        Exception lastException = null;

        while (attempt <= policy.MaxRetries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // Don't retry on cancellation
            }
            catch (Exception ex)
            {
                lastException = ex;
                attempt++;

                var errorContext = errorContextFactory(ex);
                errorContext.RetryCount = attempt;
                errorContext.MaxRetries = policy.MaxRetries;

                // Let the error handler process the error first
                if (errorHandler != null)
                {
                    try
                    {
                        var handled = await errorHandler.HandleErrorAsync(errorContext).ConfigureAwait(false);
                        if (handled)
                        {
                            _logger.Debug("Error handled by error handler, severity: {Severity}", errorContext.Severity);
                        }
                    }
                    catch (Exception handlerEx)
                    {
                        _logger.Warning(handlerEx, "Error handler threw an exception");
                    }
                }

                // Check if we should retry based on error severity
                if (errorContext.Severity == SynchronizerErrorSeverity.Fatal ||
                    errorContext.Severity == SynchronizerErrorSeverity.AuthRequired)
                {
                    _logger.Warning(ex, "Non-retryable error (severity: {Severity}), failing immediately", errorContext.Severity);
                    throw;
                }

                if (errorContext.Severity == SynchronizerErrorSeverity.Recoverable)
                {
                    _logger.Debug(ex, "Recoverable error, not retrying but allowing continuation");
                    throw;
                }

                // Transient error - check if we have retries left
                if (attempt > policy.MaxRetries)
                {
                    _logger.Warning(ex, "All {MaxRetries} retries exhausted", policy.MaxRetries);
                    throw;
                }

                // Calculate delay and wait
                var delay = errorContext.RetryDelay ?? policy.GetDelay(attempt);
                _logger.Debug("Retry attempt {Attempt}/{MaxRetries} after {Delay}ms delay for error: {ErrorMessage}",
                    attempt, policy.MaxRetries, delay.TotalMilliseconds, ex.Message);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        // Should not reach here, but just in case
        throw lastException ?? new InvalidOperationException("Retry loop completed without result");
    }

    /// <inheritdoc/>
    public async Task ExecuteWithRetryAsync(
        Func<CancellationToken, Task> operation,
        RetryPolicy policy,
        Func<Exception, SynchronizerErrorContext> errorContextFactory,
        ISynchronizerErrorHandlerFactory errorHandler = null,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync(
            async ct =>
            {
                await operation(ct).ConfigureAwait(false);
                return true; // Dummy return value
            },
            policy,
            errorContextFactory,
            errorHandler,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<T> ExecuteWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        Func<Exception, SynchronizerErrorContext> errorContextFactory,
        CancellationToken cancellationToken = default)
    {
        return ExecuteWithRetryAsync(
            operation,
            RetryPolicy.Default,
            errorContextFactory,
            null,
            cancellationToken);
    }
}
