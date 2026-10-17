#nullable enable
using System;
using System.Security.Cryptography;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.PushRelay.Socket;

/// <summary>Creates and reads the device's P-256 socket key.</summary>
public static class PushRelaySocketDeviceKeys
{
    public static PushRelaySocketDeviceKey Generate()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new PushRelaySocketDeviceKey(key.ExportPkcs8PrivateKey(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>Rebuilds the key from stored PKCS#8 bytes; null when they are not a usable P-256 key.</summary>
    public static PushRelaySocketDeviceKey? TryFromPkcs8(byte[]? pkcs8)
    {
        if (pkcs8 is null || pkcs8.Length == 0) return null;
        try
        {
            using var key = ECDiffieHellman.Create();
            key.ImportPkcs8PrivateKey(pkcs8, out var read);
            if (read != pkcs8.Length) return null;
            var curve = key.ExportParameters(false).Curve;
            if (!curve.IsNamed || curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value) return null;
            return new PushRelaySocketDeviceKey(pkcs8, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
