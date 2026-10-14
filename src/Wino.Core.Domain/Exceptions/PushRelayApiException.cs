#nullable enable
using System;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Core.Domain.Exceptions;

/// <summary>A failed relay or relay-token call. The message never contains tokens, credentials or request bodies.</summary>
public sealed class PushRelayApiException : Exception
{
    public PushRelayApiException(PushRelayProblem problem, Exception? innerException = null)
        : base(BuildMessage(problem), innerException)
    {
        Problem = problem;
    }

    public PushRelayProblem Problem { get; }

    public string Code => Problem.Code;

    public int Status => Problem.Status;

    /// <summary>401 for the relay token itself: the client gets a new token and retries once.</summary>
    public bool IsTokenRejected => Status == 401 && Problem.Reason is PushRelayAuthReasons.TokenExpired
        or PushRelayAuthReasons.AuthInvalid or PushRelayAuthReasons.AuthRequired;

    /// <summary>401 <c>device_revoked</c>: the device must register again and re-add its mailboxes.</summary>
    public bool IsDeviceRevoked => Status == 401 && Problem.Reason == PushRelayAuthReasons.DeviceRevoked;

    public bool IsRateLimited => Status == 429 || Code == PushRelayErrorCodes.RateLimited;

    /// <summary>No Wino Account session, or the Wino API refused to issue a relay token for it.</summary>
    public bool IsSignInRequired => Code == PushRelayErrorCodes.SignInRequired;

    /// <summary>
    /// Worth retrying later with backoff: 5xx, <c>auth_unavailable</c>, or a client-side transport failure.
    /// <c>provider_unreachable</c> (502) is about one mailbox and is not transient for the whole pass.
    /// </summary>
    public bool IsTransient => Code is PushRelayErrorCodes.ServiceUnavailable or PushRelayErrorCodes.InvalidResponse
        or PushRelayErrorCodes.AuthUnavailable
        || (Status >= 500 && Code != PushRelayErrorCodes.ProviderUnreachable);

    private static string BuildMessage(PushRelayProblem problem)
        => problem.Status > 0
            ? $"WinoRelay request failed with HTTP {problem.Status} ({problem.Code}{(problem.Reason is null ? string.Empty : "/" + problem.Reason)})."
            : $"WinoRelay request failed ({problem.Code}).";
}
