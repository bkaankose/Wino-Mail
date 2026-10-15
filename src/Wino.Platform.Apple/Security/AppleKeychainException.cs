using System;

namespace Wino.Platform.Apple.Security;

public enum AppleKeychainFailure
{
    Locked,
    InteractionRequired,
    Denied,
    NativeFailure
}

public sealed class AppleKeychainException(AppleKeychainFailure failure, int status)
    : Exception($"Keychain operation failed ({failure}, OSStatus {status}).")
{
    public AppleKeychainFailure Failure { get; } = failure;
    public int Status { get; } = status;
}
