#nullable enable
using System;
using System.Collections.Generic;

namespace Wino.Core.Domain.Models.PushRelay;

// Client copies of the Wino Mail API's relay device endpoints (api/v1/relay/devices...). Not part of Wino.Mail.Contracts.

/// <summary>Values of the API's attestation <c>kind</c> field.</summary>
public static class RelayDeviceAttestationKinds
{
    public const string None = "none";
    public const string AppAttest = "appattest";
    public const string PlayIntegrity = "playintegrity";
}

/// <summary>Headers that carry the device credential on API calls made without a Wino Account.</summary>
public static class RelayDeviceHeaders
{
    public const string Device = "X-Relay-Device";
    public const string Credential = "X-Relay-Device-Credential";
    public const string Assertion = "X-Relay-Device-Assertion";

    /// <summary>The challenge nonce the assertion was made over (App Attest devices only).</summary>
    public const string Nonce = "X-Relay-Device-Nonce";
}

/// <summary>An App Attest assertion for one credential call: the nonce it covers and the assertion (base64).</summary>
public sealed record RelayDeviceAssertion(string Nonce, string Assertion)
{
    public override string ToString() => "RelayDeviceAssertion { }";
}

/// <summary><c>POST api/v1/relay/devices/challenge</c>.</summary>
public sealed record RelayDeviceChallengeRequest(string Platform);

public sealed record RelayDeviceChallengeDto(string Nonce, DateTimeOffset ExpiresUtc);

public sealed record RelayDeviceAttestationRequest(string Kind, string? KeyId = null, string? AttestationObject = null, string? Token = null)
{
    public override string ToString() => $"RelayDeviceAttestationRequest {{ Kind = {Kind} }}";
}

/// <summary><c>POST api/v1/relay/devices</c>.</summary>
public sealed record RegisterRelayDeviceRequest(string Platform, string Nonce, RelayDeviceAttestationRequest Attestation);

/// <summary>A device as the API returns it. <c>Credential</c> is set only by the registration call.</summary>
public sealed record RelayDeviceDto(
    Guid DeviceId,
    string? Credential,
    string? Platform,
    string? AttestationKind,
    Guid? UserId,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? ExpiresUtc)
{
    public override string ToString() => $"RelayDeviceDto {{ DeviceId = {DeviceId}, Platform = {Platform}, Linked = {UserId is not null} }}";
}

/// <summary><c>GET api/v1/relay/devices</c>: the signed-in user's linked devices.</summary>
public sealed record RelayDeviceListDto(List<RelayDeviceDto> Devices);

/// <summary>Error codes of the API's relay device endpoints.</summary>
public static class RelayDeviceErrorCodes
{
    public const string AttestationRequired = "ATTESTATION_REQUIRED";
    public const string AttestationInvalid = "ATTESTATION_INVALID";
    public const string ChallengeExpired = "CHALLENGE_EXPIRED";
    public const string DeviceRevoked = "DEVICE_REVOKED";
    public const string DeviceNotLinked = "DEVICE_NOT_LINKED";
    public const string DeviceNotFound = "DEVICE_NOT_FOUND";
    public const string DeviceCreationLimit = "DEVICE_CREATION_LIMIT";

    /// <summary>503: the API has no attestation verifier configured for this platform right now.</summary>
    public const string AttestationUnavailable = "ATTESTATION_UNAVAILABLE";
}
