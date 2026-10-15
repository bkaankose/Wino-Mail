#nullable enable
using System;

namespace Wino.Core.Domain.Interfaces;

/// <summary>The platform push channel (APNs on Apple, FCM on Android). Windows registers a provider without a channel.</summary>
public interface IPushTokenProvider
{
    /// <summary>False when the platform has no push channel at all.</summary>
    bool IsPushChannelAvailable { get; }

    /// <summary>
    /// The current device token, or null before registration succeeds. Its format is the platform's and is
    /// opaque to shared code: APNs tokens are lowercase hex, FCM registration tokens are longer strings with
    /// other characters. Pass it to the relay unchanged.
    /// </summary>
    string? CurrentToken { get; }

    /// <summary>The last registration failure reported by the platform, for diagnostics.</summary>
    string? LastFailure { get; }

    /// <summary>Raised only when <see cref="CurrentToken"/> changes.</summary>
    event EventHandler? TokenChanged;
}
