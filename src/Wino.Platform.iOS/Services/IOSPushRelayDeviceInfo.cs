using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Platform.iOS.Services;

/// <summary>
/// Describes this iPhone or iPad to the relay. The values are read once on the main thread at registration, because
/// the relay service asks from a background thread. Since iOS 16 the device name is the generic model name
/// ("iPhone", "iPad") unless the app holds the user-assigned-device-name entitlement.
/// </summary>
public sealed class IOSPushRelayDeviceInfo(string? name, string? systemVersion) : IPushRelayDeviceInfo
{
    private readonly PushRelayDeviceDescriptor _descriptor = new(
        PushRelayPlatforms.Ios,
        string.IsNullOrWhiteSpace(name) ? null : name,
        string.IsNullOrWhiteSpace(systemVersion) ? null : "iOS " + systemVersion);

    public PushRelayDeviceDescriptor Describe() => _descriptor;
}
