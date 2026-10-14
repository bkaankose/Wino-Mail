#nullable enable
using System.Runtime.Versioning;
using Foundation;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Apple.PushRelay;

/// <summary>
/// The APNs device token. The application delegate forwards
/// <c>registeredForRemoteNotifications</c> (<see cref="SetToken"/>) and the registration failure (<see cref="SetFailure"/>);
/// the relay service listens to <see cref="TokenChanged"/>. The token is never logged.
/// </summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("ios17.0")]
public sealed class ApplePushTokenProvider : IPushTokenProvider
{
    private readonly Lock _gate = new();
    private string? _token;
    private string? _failure;

    public bool IsPushChannelAvailable => true;

    public string? CurrentToken
    {
        get { lock (_gate) return _token; }
    }

    public string? LastFailure
    {
        get { lock (_gate) return _failure; }
    }

    public event EventHandler? TokenChanged;

    /// <summary>Stores the device token as lowercase hex and raises <see cref="TokenChanged"/> when it differs.</summary>
    public void SetToken(NSData deviceToken)
    {
        ArgumentNullException.ThrowIfNull(deviceToken);
        var token = Convert.ToHexStringLower(deviceToken.ToArray());
        if (token.Length == 0) return;
        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_token, token, StringComparison.Ordinal);
            _token = token;
            _failure = null;
        }
        if (changed) TokenChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records why registration failed (for example the Simulator or a missing aps-environment entitlement).</summary>
    public void SetFailure(string? failure)
    {
        lock (_gate) _failure = string.IsNullOrWhiteSpace(failure) ? "unknown" : failure;
    }
}
