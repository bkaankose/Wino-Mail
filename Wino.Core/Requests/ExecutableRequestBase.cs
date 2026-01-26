using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests;

/// <summary>
/// Base class for executable mail requests that provides default implementations for IExecutableRequest.
/// Derived classes can override methods to customize behavior.
/// </summary>
public abstract record ExecutableMailRequestBase(MailCopy Item) : MailRequestBase(Item), IExecutableRequest
{
    /// <summary>
    /// Prepares the native request. Default implementation throws NotImplementedException.
    /// Derived classes must override this to provide actual implementation.
    /// </summary>
    public virtual Task<object> PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        throw new NotImplementedException($"PrepareNativeRequestAsync not implemented for {GetType().Name}");
    }

    /// <summary>
    /// Handles the response from the server. Default implementation does nothing.
    /// Override to process server responses (e.g., capture created message ID).
    /// </summary>
    public virtual Task HandleResponseAsync(object response, IRequestExecutionContext context)
    {
        // Default: no response handling needed
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles failure and performs rollback. Default implementation reverts UI changes.
    /// Override to add custom rollback logic (e.g., database rollback).
    /// </summary>
    public virtual Task HandleFailureAsync(Exception error, IRequestExecutionContext context)
    {
        // Default: just log the error, UI changes are already reverted by engine
        return Task.CompletedTask;
    }
}

/// <summary>
/// Base class for executable mail requests with typed native request.
/// AOT-compatible with proper type constraints.
/// </summary>
public abstract record ExecutableMailRequestBase<TNativeRequest>(MailCopy Item) 
    : ExecutableMailRequestBase(Item), IExecutableRequest<TNativeRequest>
    where TNativeRequest : class
{
    /// <summary>
    /// Prepares the typed native request.
    /// </summary>
    public abstract new Task<TNativeRequest> PrepareNativeRequestAsync(IRequestExecutionContext context);

    /// <summary>
    /// Adapts the typed PrepareNativeRequestAsync to the untyped interface.
    /// AOT-compatible type conversion.
    /// </summary>
    async Task<object> IExecutableRequest.PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        var nativeRequest = await PrepareNativeRequestAsync(context).ConfigureAwait(false);
        return nativeRequest;
    }
}

/// <summary>
/// Base class for executable mail requests with both typed native request and response.
/// AOT-compatible with proper type constraints.
/// </summary>
public abstract record ExecutableMailRequestBase<TNativeRequest, TResponse>(MailCopy Item) 
    : ExecutableMailRequestBase<TNativeRequest>(Item), IExecutableRequest<TNativeRequest, TResponse>
    where TNativeRequest : class
    where TResponse : class
{
    /// <summary>
    /// Handles the typed response from the server.
    /// </summary>
    public abstract Task HandleResponseAsync(TResponse response, IRequestExecutionContext context);

    /// <summary>
    /// Adapts the typed HandleResponseAsync to the untyped interface.
    /// AOT-compatible type checking.
    /// </summary>
    async Task IExecutableRequest.HandleResponseAsync(object response, IRequestExecutionContext context)
    {
        if (response is TResponse typedResponse)
        {
            await HandleResponseAsync(typedResponse, context).ConfigureAwait(false);
        }
        else if (response == null)
        {
            // Allow null responses
            await HandleResponseAsync(default, context).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException(
                $"Expected response of type {typeof(TResponse).Name} but got {response?.GetType().Name ?? "null"}");
        }
    }
}

/// <summary>
/// Base class for executable folder requests.
/// </summary>
public abstract record ExecutableFolderRequestBase(MailItemFolder Folder, FolderSynchronizerOperation Operation) 
    : FolderRequestBase(Folder, Operation), IExecutableRequest
{
    public virtual Task<object> PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        throw new NotImplementedException($"PrepareNativeRequestAsync not implemented for {GetType().Name}");
    }

    public virtual Task HandleResponseAsync(object response, IRequestExecutionContext context)
    {
        return Task.CompletedTask;
    }

    public virtual Task HandleFailureAsync(Exception error, IRequestExecutionContext context)
    {
        return Task.CompletedTask;
    }
}
