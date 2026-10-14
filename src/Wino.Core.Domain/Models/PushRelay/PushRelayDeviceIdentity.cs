#nullable enable
using System;

namespace Wino.Core.Domain.Models.PushRelay;

/// <summary>
/// This install's relay device. <see cref="WinoUserId"/> is the Wino Account the device was registered for, so a
/// different signed-in account registers a new device without a failing call first.
/// </summary>
public sealed record PushRelayDeviceIdentity(Guid DeviceId, Guid WinoUserId, string Platform, DateTime RegisteredAtUtc);

/// <summary>What the platform head reports about this device: relay platform value, user-visible name and OS version.</summary>
public sealed record PushRelayDeviceDescriptor(string Platform, string? Name, string? OsVersion);
