namespace Wino.Core.Domain;

/// <summary>
/// Error codes the app produces itself when a Wino Account API call cannot return a server error code.
/// </summary>
public static class WinoAccountClientErrorCodes
{
    /// <summary>The API could not be reached: no connection, DNS or TLS failure, timeout, or a gateway error page.</summary>
    public const string ServiceUnavailable = "WINO_SERVICE_UNAVAILABLE";

    /// <summary>The API answered, but the body was not a Wino API response.</summary>
    public const string InvalidServiceResponse = "WINO_SERVICE_INVALID_RESPONSE";

    /// <summary>No signed-in Wino account with usable credentials exists.</summary>
    public const string SignInRequired = "MissingAccessToken";

    /// <summary>The signed-in account changed while a request was running.</summary>
    public const string AccountSessionChanged = "AccountSessionChanged";

    /// <summary>The user dismissed an external provider's sign-in UI; nothing to show.</summary>
    public const string SignInCancelled = "WINO_SIGN_IN_CANCELLED";

    /// <summary>The external provider's sign-in failed on the device before the API was reached (the message carries the provider's text).</summary>
    public const string ExternalSignInFailed = "WINO_EXTERNAL_SIGN_IN_FAILED";

    /// <summary>The API's envelope for a rejected bearer token (any authorized endpoint).</summary>
    public const string SessionInvalid = "SESSION_INVALID";

    /// <summary>A refresh token that was already rotated was presented outside the grace window; the API revoked the whole family.</summary>
    public const string RefreshTokenReused = "REFRESH_TOKEN_REUSED";

    /// <summary>Another refresh rotated the token moments ago; the family is intact and the stored session may already be newer.</summary>
    public const string RefreshTokenSuperseded = "REFRESH_TOKEN_SUPERSEDED";

    /// <summary>The refresh token can never work again: the session is dead until the user signs in.</summary>
    public static bool IsDeadSession(string? errorCode)
        => errorCode is Wino.Mail.Api.Contracts.Common.ApiErrorCodes.RefreshTokenInvalid or RefreshTokenReused;

    public static bool IsServiceFailure(string? errorCode)
        => errorCode is ServiceUnavailable or InvalidServiceResponse;

    public static bool IsCancelled(string? errorCode) => errorCode == SignInCancelled;
}
