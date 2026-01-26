using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Requests;

namespace Wino.Core.Requests;

/// <summary>
/// Bridge adapter that wraps existing IRequestBundle to work with the new IExecutableRequest system.
/// This allows gradual migration from the old request system to the new architecture.
/// </summary>
/// <typeparam name="TNativeRequest">Type of native request (RequestInformation, IClientServiceRequest, etc.)</typeparam>
public class BridgeExecutableRequest<TNativeRequest> : IExecutableRequest<TNativeRequest>
    where TNativeRequest : class
{
    private readonly IRequestBundle<TNativeRequest> _bundle;

    public BridgeExecutableRequest(IRequestBundle<TNativeRequest> bundle)
    {
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
    }

    /// <summary>
    /// The underlying request bundle for compatibility.
    /// </summary>
    public IRequestBundle<TNativeRequest> Bundle => _bundle;

    /// <summary>
    /// Gets the request as IRequestBase for compatibility with old system.
    /// </summary>
    public IRequestBase RequestBase => _bundle.Request ?? _bundle.UIChangeRequest as IRequestBase;

    #region IRequestBase Implementation

    public object GroupingKey() => RequestBase?.GroupingKey() ?? string.Empty;

    public int ResynchronizationDelay => RequestBase?.ResynchronizationDelay ?? 0;

    #endregion

    #region IUIChangeRequest Implementation

    public void ApplyUIChanges() => _bundle.UIChangeRequest?.ApplyUIChanges();

    public void RevertUIChanges() => _bundle.UIChangeRequest?.RevertUIChanges();

    #endregion

    #region IExecutableRequest Implementation

    Task<object> IExecutableRequest.PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        // Apply UI changes (old behavior)
        _bundle.UIChangeRequest?.ApplyUIChanges();

        // Return native request as object
        return Task.FromResult<object>(_bundle.NativeRequest);
    }

    Task<TNativeRequest> IExecutableRequest<TNativeRequest>.PrepareNativeRequestAsync(IRequestExecutionContext context)
    {
        // Apply UI changes (old behavior)
        _bundle.UIChangeRequest?.ApplyUIChanges();

        // Return typed native request
        return Task.FromResult(_bundle.NativeRequest);
    }

    public Task HandleResponseAsync(object response, IRequestExecutionContext context)
    {
        // Old system: No explicit response handling
        // New system: Process response here
        // For bridge: UI changes already applied in PrepareNativeRequestAsync
        return Task.CompletedTask;
    }

    public Task HandleFailureAsync(Exception error, IRequestExecutionContext context)
    {
        // Old system: Revert UI changes on failure
        // New system: Rollback database + revert UI
        // For bridge: Just revert UI (old behavior)
        _bundle.UIChangeRequest?.RevertUIChanges();
        return Task.CompletedTask;
    }

    #endregion
}
