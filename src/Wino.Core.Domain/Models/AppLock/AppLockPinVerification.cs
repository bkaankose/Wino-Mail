#nullable enable
using System;

namespace Wino.Core.Domain.Models.AppLock;

public enum AppLockMethod
{
    Pin,
    Biometric
}

public enum AppLockPinVerificationStatus
{
    Success,
    Wrong,
    Throttled
}

/// <param name="RetryAfter">How long the user has to wait; zero unless <see cref="AppLockPinVerificationStatus.Throttled"/>.</param>
public readonly record struct AppLockPinVerification(AppLockPinVerificationStatus Status, TimeSpan RetryAfter)
{
    public bool IsSuccess => Status == AppLockPinVerificationStatus.Success;
}

public static class AppLockPinRules
{
    public const int MinimumLength = 4;
    public const int MaximumLength = 12;

    public static bool IsValid(string? pin)
    {
        if (pin is null || pin.Length < MinimumLength || pin.Length > MaximumLength) return false;

        foreach (var character in pin)
        {
            if (character is < '0' or > '9') return false;
        }

        return true;
    }
}

/// <summary>Thrown when a request is queued while the application lock has not been unlocked.</summary>
public sealed class AppLockedException() : InvalidOperationException("The application is locked.");
