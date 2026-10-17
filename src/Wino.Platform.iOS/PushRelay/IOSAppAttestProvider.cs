#nullable enable
using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeviceCheck;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.iOS.PushRelay;

/// <summary>
/// App Attest for the relay device registration. One key per install, generated on first use; the attestation binds
/// it to the API's nonce (client data hash = SHA-256 of the UTF-8 nonce), later calls assert over the client data the
/// token provider builds. Unsupported in the Simulator, where the token provider falls back to a Wino Account.
/// </summary>
[SupportedOSPlatform("ios14.0")]
public sealed class IOSAppAttestProvider : IDeviceAttestationProvider
{
    private readonly ISecureItemStore _secureItems;
    private const string Scope = "wino.push-relay";
    private const string KeyIdAccount = "app-attest-key-id";

    public IOSAppAttestProvider(ISecureItemStore secureItems) => _secureItems = secureItems;

    public DeviceAttestationKind Kind => DeviceAttestationKind.AppAttest;

    public bool IsSupported => DCAppAttestService.SharedService.Supported;

    public async Task<DeviceAttestation> AttestAsync(string nonce, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        var service = DCAppAttestService.SharedService;
        if (!service.Supported)
            throw new PlatformNotSupportedException("App Attest is not available on this device.");

        // A key can be attested once; every registration starts from a fresh key and remembers its id for assertions.
        var keyId = await service.GenerateKeyAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var clientDataHash = SHA256.HashData(Encoding.UTF8.GetBytes(nonce));
        using var hash = NSData.FromArray(clientDataHash);
        using var attestation = await service.AttestKeyAsync(keyId, hash).ConfigureAwait(false);

        _secureItems.Write(Scope, KeyIdAccount, Encoding.UTF8.GetBytes(keyId), SecureItemAccess.ThisDeviceOnlyAfterFirstUnlock);
        return new DeviceAttestation(DeviceAttestationKind.AppAttest, keyId, attestation.GetBase64EncodedString(NSDataBase64EncodingOptions.None));
    }

    public async Task<string?> CreateAssertionAsync(string keyId, byte[] clientData, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(clientData);
        var service = DCAppAttestService.SharedService;
        if (!service.Supported)
            return null;

        using var hash = NSData.FromArray(SHA256.HashData(clientData));
        using var assertion = await service.GenerateAssertionAsync(keyId, hash).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return assertion.GetBase64EncodedString(NSDataBase64EncodingOptions.None);
    }
}
