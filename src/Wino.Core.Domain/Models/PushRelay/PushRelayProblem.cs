#nullable enable
using System;

namespace Wino.Core.Domain.Models.PushRelay;

/// <summary>
/// A relay error. <see cref="Code"/> is the problem type without <c>urn:winorelay:error:</c>, or a client-side code
/// from <see cref="PushRelayErrorCodes"/>. <see cref="Status"/> is 0 when no HTTP answer arrived.
/// </summary>
public sealed record PushRelayProblem(
    string Code,
    int Status,
    string? Title = null,
    string? Detail = null,
    string? Reason = null,
    int? RetryAfterSeconds = null,
    Guid? SubscriptionId = null);

/// <summary>Relay problem codes plus the client-side codes the relay client produces.</summary>
public static class PushRelayErrorCodes
{
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string InvalidRequest = "invalid_request";
    public const string Conflict = "conflict";
    public const string RateLimited = "rate_limited";
    public const string AuthUnavailable = "auth_unavailable";
    public const string MailboxAlreadyAdded = "mailbox_already_added";
    public const string MailboxLimitReached = "mailbox_limit_reached";
    public const string ProviderAuthFailed = "provider_auth_failed";
    public const string ProviderUnreachable = "provider_unreachable";
    public const string UnsupportedProvider = "unsupported_provider";

    /// <summary>Client side: no connection, timeout, a gateway page or a 5xx without a problem body.</summary>
    public const string ServiceUnavailable = "service_unavailable";

    /// <summary>Client side: the relay answered with a body that is not relay JSON.</summary>
    public const string InvalidResponse = "invalid_response";

    /// <summary>Client side: no Wino Account session, or the API rejected it, so no relay token can be issued.</summary>
    public const string SignInRequired = "sign_in_required";
}

/// <summary>Values of the <c>reason</c> extension on relay 401 and 400 problems.</summary>
public static class PushRelayAuthReasons
{
    public const string AuthRequired = "auth_required";
    public const string AuthInvalid = "auth_invalid";
    public const string TokenExpired = "token_expired";
    public const string DeviceRevoked = "device_revoked";
    public const string DeviceIdRequired = "device_id_required";
}
