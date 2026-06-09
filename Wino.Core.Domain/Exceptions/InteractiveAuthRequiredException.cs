using System;

namespace Wino.Core.Domain.Exceptions;

/// <summary>
/// Thrown by the background companion process when an account needs interactive
/// authentication that only the UI process can perform (MSAL WAM broker with a window
/// handle, or the Google browser flow). The UI catches this, runs the interactive flow
/// locally and persists the result through the proxied account service.
/// </summary>
public class InteractiveAuthRequiredException : Exception
{
    public InteractiveAuthRequiredException(Guid accountId, string message = null)
        : base(message ?? "Interactive authentication is required.")
    {
        AccountId = accountId;
    }

    public Guid AccountId { get; }
}
