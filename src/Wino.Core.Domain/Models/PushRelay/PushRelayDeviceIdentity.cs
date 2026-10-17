#nullable enable
using System;

namespace Wino.Core.Domain.Models.PushRelay;

/// <summary>
/// This install's relay device. The device is registered on its own identity; <see cref="WinoUserId"/> is the Wino
/// Account that was signed in when it registered, if any, kept for diagnostics only. Ownership moves through the API
/// link call, never by registering a second device.
/// </summary>
public sealed record PushRelayDeviceIdentity(Guid DeviceId, Guid? WinoUserId, string Platform, DateTime RegisteredAtUtc);

/// <summary>What the platform head reports about this device: relay platform value, user-visible name and OS version.</summary>
public sealed record PushRelayDeviceDescriptor(string Platform, string? Name, string? OsVersion);
