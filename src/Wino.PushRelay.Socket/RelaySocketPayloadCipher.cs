#nullable enable
using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.PushRelay.Socket;

/// <summary>
/// Per-device socket payload encryption, the device side of <c>WinoRelay.Sockets.SocketPayloadCipher</c>: static ECDH (P-256)
/// between the device key and the relay key named by <c>kid</c>, HKDF-SHA256 (salt = kid, info = <c>winorelay-socket-v1|deviceId</c>)
/// to a 32-byte AES key, AES-256-GCM per message with the event id as associated data. The relay's public keys come from
/// <c>/.well-known/winorelay</c>; the device's private key from <see cref="Wino.Core.Domain.Interfaces.IPushRelaySocketKeyStore"/>.
/// <see cref="Encrypt"/> and <see cref="DeriveRelayKey"/> mirror the relay for tests and tools.
/// </summary>
public static class RelaySocketPayloadCipher
{
    private const string Info = "winorelay-socket-v1|";
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;

    public static bool IsEnvelope(IReadOnlyDictionary<string, string> message)
        => message.ContainsKey(RelaySocketEnvelopeKeys.Version);

    /// <summary>Derives the device's AES key for one relay key.</summary>
    public static byte[] DeriveKey(byte[] devicePkcs8PrivateKey, PushRelaySocketKey relayKey, Guid deviceId)
    {
        ArgumentNullException.ThrowIfNull(devicePkcs8PrivateKey);
        ArgumentNullException.ThrowIfNull(relayKey);
        if (relayKey.Crv is not null and not "P-256" || relayKey.Kty is not null and not "EC")
            throw new RelaySocketCryptoException($"Relay key '{relayKey.Kid}' is not a P-256 EC key.");

        using var device = ECDiffieHellman.Create();
        device.ImportPkcs8PrivateKey(devicePkcs8PrivateKey, out _);
        using var relay = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = Base64Url.DecodeFromChars(relayKey.X), Y = Base64Url.DecodeFromChars(relayKey.Y) },
        });
        return Derive(device, relay.PublicKey, relayKey.Kid, deviceId);
    }

    /// <summary>The relay's side of <see cref="DeriveKey"/>: from the relay private key and the device's registered public key.</summary>
    public static byte[] DeriveRelayKey(byte[] relayPkcs8PrivateKey, string kid, string deviceSpkiBase64, Guid deviceId)
    {
        using var relay = ECDiffieHellman.Create();
        relay.ImportPkcs8PrivateKey(relayPkcs8PrivateKey, out _);
        using var device = ECDiffieHellman.Create();
        device.ImportSubjectPublicKeyInfo(Convert.FromBase64String(deviceSpkiBase64), out _);
        return Derive(relay, device.PublicKey, kid, deviceId);
    }

    private static byte[] Derive(ECDiffieHellman own, ECDiffieHellmanPublicKey other, string kid, Guid deviceId)
    {
        var secret = own.DeriveRawSecretAgreement(other);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, KeyLength, Encoding.UTF8.GetBytes(kid), Encoding.UTF8.GetBytes(Info + deviceId.ToString("D")));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Decrypts an envelope into the payload dictionary. The envelope's <c>eid</c> is the associated data and must match
    /// the <c>eid</c> inside the payload, so a relay cannot be made to re-label an event.
    /// </summary>
    /// <exception cref="RelaySocketCryptoException">A malformed envelope, a wrong key or a tampered message.</exception>
    public static Dictionary<string, string> Decrypt(IReadOnlyDictionary<string, string> envelope, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(key);

        if (!envelope.TryGetValue(RelaySocketEnvelopeKeys.Version, out var version) || version != RelaySocketEnvelopeKeys.SupportedVersion)
            throw new RelaySocketCryptoException($"Unsupported envelope version '{version}'.");
        if (!envelope.TryGetValue(RelaySocketEnvelopeKeys.EventId, out var eventId) || string.IsNullOrEmpty(eventId))
            throw new RelaySocketCryptoException("The envelope carries no event id (eid), which is the associated data.");
        if (!envelope.TryGetValue(RelaySocketEnvelopeKeys.Nonce, out var nonceText) || !envelope.TryGetValue(RelaySocketEnvelopeKeys.Ciphertext, out var cipherText))
            throw new RelaySocketCryptoException("The envelope has no nonce or ciphertext.");

        byte[] nonce, cipher;
        try
        {
            nonce = Base64Url.DecodeFromChars(nonceText);
            cipher = Base64Url.DecodeFromChars(cipherText);
        }
        catch (FormatException error)
        {
            throw new RelaySocketCryptoException("The envelope is not base64url.", error);
        }

        if (nonce.Length != NonceLength || cipher.Length < TagLength)
            throw new RelaySocketCryptoException("The envelope nonce or ciphertext has the wrong length.");

        var plain = new byte[cipher.Length - TagLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length, TagLength), plain, Encoding.UTF8.GetBytes(eventId));
        }
        catch (CryptographicException error)
        {
            throw new RelaySocketCryptoException("The event could not be authenticated with this device's key.", error);
        }

        Dictionary<string, string> payload;
        try
        {
            payload = ParsePayload(plain);
        }
        catch (JsonException error)
        {
            throw new RelaySocketCryptoException("The decrypted event is not a JSON object.", error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        if (payload.TryGetValue(PushRelayPayloadKeys.EventId, out var innerId) && innerId != eventId)
            throw new RelaySocketCryptoException("The envelope event id does not match the payload.");

        payload.TryAdd(PushRelayPayloadKeys.EventId, eventId);
        return payload;
    }

    /// <summary>Encrypts a payload the way the relay does (tests and tools): <c>{ enc, kid, eid, n, c }</c>.</summary>
    public static Dictionary<string, string> Encrypt(byte[] key, string kid, string payloadJson, string eventId)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var plain = Encoding.UTF8.GetBytes(payloadJson);
        var cipher = new byte[plain.Length + TagLength];
        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plain, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length, TagLength), Encoding.UTF8.GetBytes(eventId));
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RelaySocketEnvelopeKeys.Version] = RelaySocketEnvelopeKeys.SupportedVersion,
            [RelaySocketEnvelopeKeys.KeyId] = kid,
            [RelaySocketEnvelopeKeys.EventId] = eventId,
            [RelaySocketEnvelopeKeys.Nonce] = Base64Url.EncodeToString(nonce),
            [RelaySocketEnvelopeKeys.Ciphertext] = Base64Url.EncodeToString(cipher),
        };
    }

    /// <summary>A payload JSON object as the push dictionary: strings as they are, every other value as its JSON text.</summary>
    public static Dictionary<string, string> ParsePayload(ReadOnlySpan<byte> json)
    {
        var fields = JsonSerializer.Deserialize(json, RelaySocketJsonContext.Default.DictionaryStringJsonElement)
            ?? throw new JsonException("Payload is null.");
        var payload = new Dictionary<string, string>(fields.Count, StringComparer.Ordinal);
        foreach (var (name, value) in fields)
            payload[name] = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        return payload;
    }
}

/// <summary>A socket event that could not be decrypted or authenticated.</summary>
public sealed class RelaySocketCryptoException : Exception
{
    public RelaySocketCryptoException(string message, Exception? innerException = null) : base(message, innerException) { }
}
