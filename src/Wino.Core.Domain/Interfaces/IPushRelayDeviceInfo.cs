#nullable enable
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Core.Domain.Interfaces;

/// <summary>Platform description of this device for the relay: platform value, user-visible name and OS version.</summary>
public interface IPushRelayDeviceInfo
{
    PushRelayDeviceDescriptor Describe();
}
