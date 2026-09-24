#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Intelligence;
using Wino.Core.Domain.Intelligence.Keys;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.Cryptography;

namespace Wino.Services;

/// <summary>
/// Device result keys in the intelligence database. The private key is PKCS#8 wrapped by
/// DPAPI with the Wino user id as entropy, so a copied database is useless on another
/// machine, another Windows profile or for another Wino account.
/// </summary>
internal sealed class IntelligenceResultKeyStore : IIntelligenceResultKeyStore, IDisposable
{
    /// <summary>A result page never approaches this; it only bounds a hostile payload.</summary>
    private const int MaximumResultCiphertextBytes = 64 * 1024 * 1024;

    private readonly IIntelligenceResultKeyRows _rows;
    private readonly IIntelligenceKeyProtector _protector;
    private readonly IntelligenceResultKeyPresence _presence;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _createLock = new(1, 1);

    public IntelligenceResultKeyStore(
        IIntelligenceResultKeyRows rows,
        IIntelligenceKeyProtector protector,
        IntelligenceResultKeyPresence presence,
        TimeProvider? time = null)
    {
        _rows = rows;
        _protector = protector;
        _presence = presence;
        _time = time ?? TimeProvider.System;
    }

    public async Task<IntelligenceResultKey?> GetActiveKeyAsync(Guid winoUserId, CancellationToken cancellationToken = default)
    {
        var rows = await _rows.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var row = rows.LastOrDefault(x => x.WinoUserId == winoUserId && x.Status == IntelligenceResultKeyStatuses.Active);
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<IntelligenceResultKey>> GetKeysAsync(CancellationToken cancellationToken = default)
        => [.. (await _rows.GetAllAsync(cancellationToken).ConfigureAwait(false)).Select(Map)];

    public async Task<IntelligenceResultKey> GetOrCreateAsync(Guid winoUserId, CancellationToken cancellationToken = default)
    {
        if (winoUserId == Guid.Empty)
        {
            throw new ArgumentException("A Wino user id is required.", nameof(winoUserId));
        }

        // Serialised so parallel refreshes that all see "no key" still create exactly one.
        await _createLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await GetActiveKeyAsync(winoUserId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }

            // Marked before the insert: if the process dies in between, the next removal
            // still knows to look.
            _presence.MarkPresent();
            var row = CreateRow(winoUserId, _time.GetUtcNow());
            await _rows.InsertAsync(row, cancellationToken).ConfigureAwait(false);
            return Map(row);
        }
        finally
        {
            _createLock.Release();
        }
    }

    public async Task<byte[]> DecryptAsync(
        string keyId,
        ReadOnlyMemory<byte> encodedEnvelope,
        Guid mailboxId,
        string route,
        CancellationToken cancellationToken = default)
    {
        var row = await _rows.GetAsync(keyId, cancellationToken).ConfigureAwait(false)
            ?? throw new IntelligenceResultKeyLostException(keyId);

        EncryptedContentEnvelope envelope;
        try
        {
            envelope = ContentEnvelopeBinaryCodec.Decode(encodedEnvelope.Span, MaximumResultCiphertextBytes);
        }
        catch (FormatException exception)
        {
            throw new IntelligenceResultKeyLostException(keyId, exception);
        }

        if (!string.Equals(envelope.KeyId, keyId, StringComparison.Ordinal))
        {
            throw new IntelligenceResultKeyLostException(keyId);
        }

        byte[]? pkcs8 = null;
        char[]? pem = null;
        try
        {
            pkcs8 = _protector.Unprotect(row.PrivateKeyProtected, Entropy(row.WinoUserId));
            pem = PemEncoding.Write("PRIVATE KEY", pkcs8);

            // The decryptor takes PEM as a string, which cannot be cleared. It lives only for
            // this one page and is unreachable as soon as the call returns.
            var decryptor = new PemContentEnvelopeDecryptor(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [row.KeyId] = new string(pem),
            });
            return decryptor.Decrypt(envelope, new ContentEnvelopeContext(row.WinoUserId, mailboxId, route));
        }
        catch (CryptographicException exception)
        {
            throw new IntelligenceResultKeyLostException(keyId, exception);
        }
        catch (PlatformNotSupportedException exception)
        {
            throw new IntelligenceResultKeyLostException(keyId, exception);
        }
        finally
        {
            if (pkcs8 is not null) CryptographicOperations.ZeroMemory(pkcs8);
            if (pem is not null) Array.Clear(pem);
            CryptographicOperations.ZeroMemory(envelope.WrappedKey);
            CryptographicOperations.ZeroMemory(envelope.Ciphertext);
        }
    }

    public Task DeleteAsync(string keyId, CancellationToken cancellationToken = default)
        => _rows.DeleteAsync(keyId, cancellationToken);

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await _rows.DeleteAllAsync(cancellationToken).ConfigureAwait(false);
        _presence.MarkAbsent();
    }

    public void Dispose() => _createLock.Dispose();

    private IntelligenceResultKeyRow CreateRow(Guid winoUserId, DateTimeOffset now)
    {
        using var rsa = RSA.Create(IntelligenceResultKeyIds.KeySizeInBits);
        var spki = rsa.ExportSubjectPublicKeyInfo();
        var pkcs8 = rsa.ExportPkcs8PrivateKey();
        try
        {
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(spki));
            return new IntelligenceResultKeyRow
            {
                KeyId = IntelligenceResultKeyIds.Create(fingerprint, now),
                WinoUserId = winoUserId,
                PublicKeyPem = PemEncoding.WriteString("PUBLIC KEY", spki),
                PrivateKeyProtected = _protector.Protect(pkcs8, Entropy(winoUserId)),
                Fingerprint = fingerprint,
                CreatedUtc = now.UtcDateTime,
                Status = IntelligenceResultKeyStatuses.Active,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    private static byte[] Entropy(Guid winoUserId)
        => SHA256.HashData(Encoding.UTF8.GetBytes($"Wino.IntelligenceResultKey.{winoUserId:D}"));

    private static IntelligenceResultKey Map(IntelligenceResultKeyRow row)
        => new(row.KeyId, row.WinoUserId, row.PublicKeyPem, row.CreatedUtc);
}

/// <summary>How a device result key is named. The API checks the fingerprint suffix.</summary>
public static class IntelligenceResultKeyIds
{
    public const int KeySizeInBits = 3072;

    /// <summary>"dev-{yyyyMM}-{first 8 hex of SHA-256(SubjectPublicKeyInfo)}".</summary>
    public static string Create(string fingerprintHex, DateTimeOffset createdUtc)
        => string.Create(CultureInfo.InvariantCulture, $"dev-{createdUtc.UtcDateTime:yyyyMM}-{fingerprintHex[..8].ToLowerInvariant()}");
}

/// <summary>DPAPI for the current Windows user, the same primitive the DAV credential store uses.</summary>
internal sealed class DpapiIntelligenceKeyProtector : IIntelligenceKeyProtector
{
#pragma warning disable CA1416
    public byte[] Protect(byte[] data, byte[] entropy)
        => ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data, byte[] entropy)
        => ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
}
