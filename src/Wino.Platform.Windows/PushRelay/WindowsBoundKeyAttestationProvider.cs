#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Windows.PushRelay;

/// <summary>
/// The relay device's bound key: a persisted, non-exportable CNG ECDSA P-256 key in the user's key store, held by the
/// TPM (Microsoft Platform Crypto Provider) when one is usable and by the software key storage provider otherwise. The
/// registration sends its public key and a signature over the API nonce; every later credential call signs
/// "{deviceId}|{nonce}". The key id is the CNG key name.
/// </summary>
public sealed class WindowsBoundKeyAttestationProvider : IDeviceAttestationProvider
{
    public const string KeyName = "Wino.Mail.RelayDevice";

    private static readonly CngProvider PlatformProvider = new("Microsoft Platform Crypto Provider");
    private static readonly CngProvider[] Providers = [PlatformProvider, CngProvider.MicrosoftSoftwareKeyStorageProvider];

    private readonly ILogger _logger = Log.ForContext<WindowsBoundKeyAttestationProvider>();

    public DeviceAttestationKind Kind => DeviceAttestationKind.BoundKey;

    public bool IsSupported => true;

    public Task<DeviceAttestation> AttestAsync(string nonce, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        // TPM key creation and signing take hundreds of milliseconds.
        return Task.Run(() => Attest(nonce), cancellationToken);
    }

    public Task<string?> CreateAssertionAsync(string keyId, byte[] clientData, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(clientData);

        return Task.Run(() =>
        {
            using var key = OpenKey(keyId);
            if (key is null)
                return null;

            using var ecdsa = new ECDsaCng(key);
            return (string?)Convert.ToBase64String(ecdsa.SignData(clientData, HashAlgorithmName.SHA256));
        }, cancellationToken);
    }

    /// <summary>
    /// Every registration starts from a fresh key; the previous one belongs to a device the API no longer uses. A key
    /// that cannot be deleted keeps its name and the new key gets a unique one.
    /// </summary>
    private DeviceAttestation Attest(string nonce)
    {
        var name = KeyName;
        if (!TryDeleteKey(name))
            name = $"{KeyName}.{Guid.NewGuid():N}";

        using var key = CreateKey(name);
        using var ecdsa = new ECDsaCng(key);
        var publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        var signature = Convert.ToBase64String(ecdsa.SignData(Encoding.UTF8.GetBytes(nonce), HashAlgorithmName.SHA256));
        return new DeviceAttestation(DeviceAttestationKind.BoundKey, KeyId: name, PublicKey: publicKey, Signature: signature);
    }

    private CngKey CreateKey(string name)
    {
        try
        {
            var key = CngKey.Create(CngAlgorithm.ECDsaP256, name, CreationParameters(PlatformProvider));
            _logger.Information("Created the relay device key in the TPM.");
            return key;
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            // No TPM, a TPM without ECC, a VM, or a policy block.
            _logger.Information("The TPM cannot hold the relay device key ({Error} 0x{HResult:X8}); using the software key store.", ex.GetType().Name, ex.HResult);
        }

        var softwareKey = CngKey.Create(CngAlgorithm.ECDsaP256, name, CreationParameters(CngProvider.MicrosoftSoftwareKeyStorageProvider));
        _logger.Information("Created the relay device key in the software key store.");
        return softwareKey;
    }

    private static CngKeyCreationParameters CreationParameters(CngProvider provider) => new()
    {
        Provider = provider,
        KeyUsage = CngKeyUsages.Signing,
        ExportPolicy = CngExportPolicies.None,
        KeyCreationOptions = CngKeyCreationOptions.None,
    };

    private static CngKey? OpenKey(string name)
    {
        foreach (var provider in Providers)
        {
            if (!Exists(name, provider))
                continue;

            try { return CngKey.Open(name, provider); }
            catch (CryptographicException) { }
        }

        return null;
    }

    private static bool Exists(string name, CngProvider provider)
    {
        try { return CngKey.Exists(name, provider); }
        catch (CryptographicException)
        {
            // The TPM provider fails to open on machines without a usable TPM.
            return false;
        }
    }

    private bool TryDeleteKey(string name)
    {
        var deleted = true;
        foreach (var provider in Providers)
        {
            if (!Exists(name, provider))
                continue;

            try
            {
                using var key = CngKey.Open(name, provider);
                key.Delete();
            }
            catch (CryptographicException ex)
            {
                _logger.Warning("The previous relay device key could not be deleted (0x{HResult:X8}).", ex.HResult);
                deleted = false;
            }
        }

        return deleted;
    }
}
