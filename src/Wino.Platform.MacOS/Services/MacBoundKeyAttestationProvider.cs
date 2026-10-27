using System.Security.Cryptography;
using System.Text;
using Foundation;
using Security;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.MacOS.Services;

/// <summary>
/// The relay device's bound key: a P-256 key in the Secure Enclave when the Mac has one and the build may use it, and
/// otherwise a software key whose PKCS#8 bytes live in the Keychain. The registration sends its public key and a
/// signature over the API nonce; every later credential call signs "{deviceId}|{nonce}". The key id says where the key
/// lives: "se:&lt;application tag&gt;" or "sw:&lt;keychain account&gt;".
/// </summary>
public sealed class MacBoundKeyAttestationProvider : IDeviceAttestationProvider
{
    private const string ApplicationTag = "com.winomail.relay-device";
    private const string Scope = "wino.push-relay";
    private const string SoftwareAccount = "bound-key-v1";
    private const string EnclavePrefix = "se:";
    private const string SoftwarePrefix = "sw:";

    private readonly ISecureItemStore _secureItems;
    private readonly ILogger _logger = Log.ForContext<MacBoundKeyAttestationProvider>();

    public MacBoundKeyAttestationProvider(ISecureItemStore secureItems) => _secureItems = secureItems;

    public DeviceAttestationKind Kind => DeviceAttestationKind.BoundKey;

    public bool IsSupported => true;

    public Task<DeviceAttestation> AttestAsync(string nonce, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);
        return Task.Run(() => Attest(nonce), cancellationToken);
    }

    public Task<string?> CreateAssertionAsync(string keyId, byte[] clientData, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(clientData);

        return Task.Run(() =>
        {
            byte[]? signature;
            if (keyId.StartsWith(EnclavePrefix, StringComparison.Ordinal))
            {
                using var key = LoadEnclaveKey(keyId[EnclavePrefix.Length..]);
                signature = key is null ? null : SignWithEnclave(key, clientData);
            }
            else if (keyId.StartsWith(SoftwarePrefix, StringComparison.Ordinal))
            {
                signature = SignWithSoftwareKey(keyId[SoftwarePrefix.Length..], clientData);
            }
            else
            {
                signature = null;
            }

            return signature is null ? null : Convert.ToBase64String(signature);
        }, cancellationToken);
    }

    /// <summary>Every registration starts from a fresh key; the previous one belongs to a device the API no longer uses.</summary>
    private DeviceAttestation Attest(string nonce)
    {
        var data = Encoding.UTF8.GetBytes(nonce);
        DeleteEnclaveKey(ApplicationTag);
        DeleteSoftwareKey();

        try
        {
            using var key = CreateEnclaveKey(ApplicationTag);
            var publicKey = ExportSubjectPublicKeyInfo(key);
            var signature = SignWithEnclave(key, data);
            _logger.Information("Created the relay device key in the Secure Enclave.");
            return new DeviceAttestation(DeviceAttestationKind.BoundKey, KeyId: EnclavePrefix + ApplicationTag,
                PublicKey: Convert.ToBase64String(publicKey), Signature: Convert.ToBase64String(signature));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An Intel Mac without a T2 chip, a VM, or a build without the keychain entitlement the Secure Enclave needs.
            _logger.Information("The Secure Enclave cannot hold the relay device key ({Error}); using a Keychain key.", ex.Message);
            DeleteEnclaveKey(ApplicationTag);
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pkcs8 = ecdsa.ExportPkcs8PrivateKey();
        try
        {
            _secureItems.Write(Scope, SoftwareAccount, pkcs8, SecureItemAccess.ThisDeviceOnlyAfterFirstUnlock);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        _logger.Information("Created the relay device key in the Keychain.");
        return new DeviceAttestation(DeviceAttestationKind.BoundKey, KeyId: SoftwarePrefix + SoftwareAccount,
            PublicKey: Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()),
            Signature: Convert.ToBase64String(ecdsa.SignData(data, HashAlgorithmName.SHA256)));
    }

    private static SecKey CreateEnclaveKey(string tag)
    {
        // After first unlock, like the device credential: the relay token renews while the screen is locked.
        using var access = new SecAccessControl(SecAccessible.AfterFirstUnlockThisDeviceOnly, SecAccessControlCreateFlags.PrivateKeyUsage);
        using var applicationTag = NSData.FromString(tag, NSStringEncoding.UTF8);
        var parameters = new SecKeyGenerationParameters
        {
            KeyType = SecKeyType.ECSecPrimeRandom,
            KeySizeInBits = 256,
            TokenID = SecTokenID.SecureEnclave,
            PrivateKeyAttrs = new SecKeyParameters
            {
                IsPermanent = true,
                ApplicationTag = applicationTag,
                AccessControl = access,
            },
        };

        var key = SecKey.CreateRandomKey(parameters, out var error);
        return key ?? throw new CryptographicException($"Secure Enclave key creation failed ({error?.Code}).");
    }

    private static SecRecord EnclaveKeyQuery(string tag) => new(SecKind.Key)
    {
        KeyClass = SecKeyClass.Private,
        KeyType = SecKeyType.ECSecPrimeRandom,
        ApplicationTag = NSData.FromString(tag, NSStringEncoding.UTF8),
        UseDataProtectionKeychain = true,
    };

    private static SecKey? LoadEnclaveKey(string tag)
    {
        using var query = EnclaveKeyQuery(tag);
        var result = SecKeyChain.QueryAsConcreteType(query, out var status);
        return status == SecStatusCode.Success ? result as SecKey : null;
    }

    private void DeleteEnclaveKey(string tag)
    {
        using var query = EnclaveKeyQuery(tag);
        var status = SecKeyChain.Remove(query);
        if (status is not (SecStatusCode.Success or SecStatusCode.ItemNotFound))
            _logger.Information("The previous Secure Enclave relay device key could not be deleted ({Status}).", status);
    }

    private void DeleteSoftwareKey()
    {
        try
        {
            _secureItems.Delete(Scope, SoftwareAccount);
        }
        catch (Exception ex)
        {
            _logger.Information("The previous Keychain relay device key could not be deleted ({Error}).", ex.GetType().Name);
        }
    }

    private static byte[] SignWithEnclave(SecKey key, byte[] data)
    {
        // DER-encoded (X9.62) ECDSA, which the API accepts next to IEEE P1363.
        using var message = NSData.FromArray(data);
        using var signature = key.CreateSignature(SecKeyAlgorithm.EcdsaSignatureMessageX962Sha256, message, out var error);
        return signature?.ToArray() ?? throw new CryptographicException($"Secure Enclave signing failed ({error?.Code}).");
    }

    private byte[]? SignWithSoftwareKey(string account, byte[] data)
    {
        var pkcs8 = _secureItems.Read(Scope, account);
        if (pkcs8 is null || pkcs8.Length == 0)
            return null;

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(pkcs8, out _);
            return ecdsa.SignData(data, HashAlgorithmName.SHA256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>The Security framework exports an X9.63 point (04 || X || Y); the API takes a DER SubjectPublicKeyInfo.</summary>
    private static byte[] ExportSubjectPublicKeyInfo(SecKey privateKey)
    {
        using var publicKey = privateKey.GetPublicKey() ?? throw new CryptographicException("The Secure Enclave key has no public key.");
        using var external = publicKey.GetExternalRepresentation(out var error)
            ?? throw new CryptographicException($"The Secure Enclave public key could not be exported ({error?.Code}).");
        var point = external.ToArray();
        if (point.Length != 65 || point[0] != 0x04)
            throw new CryptographicException("The Secure Enclave public key is not an uncompressed P-256 point.");

        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..65] },
        });
        return ecdsa.ExportSubjectPublicKeyInfo();
    }
}
