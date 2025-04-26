using System.Threading.Tasks;
using Wino.Core.Domain.Models.Errors;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// Interface for handling specific synchronizer errors
/// </summary>
public interface ISynchronizerErrorHandler
{
    /// <summary>
    /// Determines if this handler can handle the specified error
    /// </summary>
    /// <param name="error">The error to check</param>
    /// <returns>True if this handler can handle the error, false otherwise</returns>
    bool CanHandle(SynchronizerErrorContext error);

    /// <summary>
    /// Handles the specified error
    /// </summary>
    /// <param name="error">The error to handle</param>
    /// <returns>A task that completes when the error is handled</returns>
    Task<bool> HandleAsync(SynchronizerErrorContext error);
}

public interface ISynchronizerErrorHandlerFactory
{
    Task<bool> HandleErrorAsync(SynchronizerErrorContext error);
}

public interface IOutlookSynchronizerErrorHandlerFactory : ISynchronizerErrorHandlerFactory;
public interface IGmailSynchronizerErrorHandlerFactory : ISynchronizerErrorHandlerFactory;
