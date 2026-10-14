#nullable enable
using System;

namespace Wino.Core.Domain.Interfaces;

/// <summary>The platform push channel (APNs on Apple). Windows registers a provider without a channel.</summary>
public interface IPushTokenProvider
{
    /// <summary>False when the platform has no push channel at all.</summary>
    bool IsPushChannelAvailable { get; }

    /// <summary>The current device token as lowercase hex, or null before registration succeeds.</summary>
    string? CurrentToken { get; }

    /// <summary>The last registration failure reported by the platform, for diagnostics.</summary>
    string? LastFailure { get; }

    /// <summary>Raised only when <see cref="CurrentToken"/> changes.</summary>
    event EventHandler? TokenChanged;
}
