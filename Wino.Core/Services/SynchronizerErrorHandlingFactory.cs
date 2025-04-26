using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Errors;

namespace Wino.Core.Services;

/// <summary>
/// Factory for handling synchronizer errors
/// </summary>
public class SynchronizerErrorHandlingFactory
{
    private readonly ILogger _logger = Log.ForContext<SynchronizerErrorHandlingFactory>();
    private readonly List<ISynchronizerErrorHandler> _handlers = new();

    /// <summary>
    /// Registers an error handler
    /// </summary>
    /// <param name="handler">The handler to register</param>
    public void RegisterHandler(ISynchronizerErrorHandler handler)
    {
        _handlers.Add(handler);
    }

    /// <summary>
    /// Handles an error using the registered handlers
    /// </summary>
    /// <param name="error">The error to handle</param>
    /// <returns>True if the error was handled, false otherwise</returns>
    public async Task<bool> HandleErrorAsync(SynchronizerErrorContext error)
    {
        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(error))
            {
                _logger.Debug("Found handler {HandlerType} for error code {ErrorCode} message {ErrorMessage}",
                    handler.GetType().Name, error.ErrorCode, error.ErrorMessage);

                return await handler.HandleAsync(error);
            }
        }

        _logger.Debug("No handler found for error code {ErrorCode} message {ErrorMessage}",
            error.ErrorCode, error.ErrorMessage);

        return false;
    }
}
