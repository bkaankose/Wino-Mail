#nullable enable
using System;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.PushRelay;

/// <summary>
/// This install's device identity at the Wino Mail API: the id the API and the relay share, the opaque secret that
/// gets relay tokens without a Wino Account, and the Wino Account the device is linked to, when any. Stored in the
/// platform secure store; never roams.
/// </summary>
public sealed record PushRelayDeviceCredential(
    Guid DeviceId,
    string Secret,
    string Platform,
    DeviceAttestationKind AttestationKind,
    string? AttestationKeyId,
    Guid? LinkedWinoUserId,
    DateTime CreatedUtc)
{
    /// <summary>Never prints the secret.</summary>
    public override string ToString()
        => $"PushRelayDeviceCredential {{ DeviceId = {DeviceId}, Platform = {Platform}, Attestation = {AttestationKind}, Linked = {LinkedWinoUserId is not null} }}";
}
