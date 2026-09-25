#nullable enable
using System;
using System.Net;

namespace Wino.Core.Domain.Exceptions;

/// <summary>
/// A Wino Account API request failed. <see cref="Exception.Message"/> carries the stable error code
/// so existing callers can keep translating it with <see cref="WinoAccountApiErrorTranslator"/>.
/// </summary>
public class WinoAccountApiException : InvalidOperationException
{
    public WinoAccountApiException(string errorCode, HttpStatusCode? statusCode = null, Exception? innerException = null)
        : base(errorCode, innerException)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    public string ErrorCode { get; }

    public HttpStatusCode? StatusCode { get; }

    /// <summary>The service could not be reached or answered with something other than the API.</summary>
    public bool IsServiceUnavailable => WinoAccountClientErrorCodes.IsServiceFailure(ErrorCode);

    public static WinoAccountApiException ServiceUnavailable(Exception? innerException = null, HttpStatusCode? statusCode = null)
        => new(WinoAccountClientErrorCodes.ServiceUnavailable, statusCode, innerException);

    public static WinoAccountApiException InvalidResponse(HttpStatusCode statusCode, Exception? innerException = null)
        => new(WinoAccountClientErrorCodes.InvalidServiceResponse, statusCode, innerException);

    public static WinoAccountApiException SignInRequired()
        => new(WinoAccountClientErrorCodes.SignInRequired);
}
