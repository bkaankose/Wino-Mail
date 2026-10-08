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

    public static bool IsServiceFailure(string? errorCode)
        => errorCode is ServiceUnavailable or InvalidServiceResponse;
}
