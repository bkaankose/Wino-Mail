using System;
using Wino.Core.Domain.Exceptions;

namespace Wino.Platform.MacOS.Security;

public enum MacOSKeychainFailure
{
    Locked,
    InteractionRequired,
    Denied,
    NativeFailure
}

public sealed class MacOSKeychainException(MacOSKeychainFailure failure, int status)
    : Exception($"Keychain operation failed ({failure}, OSStatus {status}).")
{
    public MacOSKeychainFailure Failure { get; } = failure;
    public int Status { get; } = status;
}

public sealed class MacOSCredentialMissingException()
    : AccountCredentialMissingException;
