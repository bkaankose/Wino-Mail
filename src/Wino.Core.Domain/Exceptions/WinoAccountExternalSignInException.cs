#nullable enable
using System;

namespace Wino.Core.Domain.Exceptions;

/// <summary>
/// A device-side failure of an external sign-in provider (Apple's sheet, Google Credential Manager, the web session),
/// raised before the Wino API is reached. <see cref="ErrorCode"/> is a Wino Account error code the dialogs translate;
/// the message is the provider's text, shown under it.
/// </summary>
public sealed class WinoAccountExternalSignInException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
