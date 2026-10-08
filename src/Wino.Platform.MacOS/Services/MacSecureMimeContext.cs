using MimeKit.Cryptography;
using Org.BouncyCastle.Pkix;
using Org.BouncyCastle.X509;

namespace Wino.Platform.MacOS.Services;

/// <summary>One certificate held by the Wino S/MIME store, already decrypted for this process.</summary>
/// <param name="Thumbprint">Upper-case SHA-1 thumbprint of the leaf certificate.</param>
/// <param name="Purpose">Personal (signing/decryption) or Recipient (encryption to others).</param>
/// <param name="StorePassword">Random password of the re-wrapped PKCS#12 payload; empty for a public certificate.</param>
/// <param name="Payload">PKCS#12 bytes (leaf, chain and private key) or a DER certificate.</param>
/// <param name="LeafDer">DER of the certificate the entry represents.</param>
internal sealed record MacSmimeStoreEntry(string Thumbprint, Wino.Core.Domain.Enums.SmimeCertificatePurpose Purpose, bool HasPrivateKey,
    string StorePassword, byte[] Payload, byte[] LeafDer);

/// <summary>
/// The S/MIME context for one operation. MimeKit's BouncyCastle implementation keeps keys and
/// certificates in memory; the application store (<see cref="MacSmimeCertificateService"/>) owns the
/// encrypted copies on disk and loads them into each new context. Nothing goes to the Keychain.
/// </summary>
/// <remarks>
/// MimeKit's <c>DefaultSecureMimeContext</c> needs System.Data.SQLite or Mono.Data.Sqlite, which this
/// app does not ship, so the persistent store is Wino's own protected file set instead.
/// </remarks>
internal sealed class MacSecureMimeContext : TemporarySecureMimeContext
{
    public MacSecureMimeContext(IEnumerable<MacSmimeStoreEntry> entries, CancellationToken cancellationToken = default)
    {
        var parser = new X509CertificateParser();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.HasPrivateKey)
            {
                using var stream = new MemoryStream(entry.Payload, writable: false);
                Import(stream, entry.StorePassword, cancellationToken);
            }
            else
            {
                Import(parser.ReadCertificate(entry.Payload), cancellationToken);
            }
        }
    }

    /// <summary>
    /// MimeKit's temporary context only trusts self-signed certificates that were imported into it, so a
    /// signature from a public CA would never chain to a root. Add the macOS system roots (loaded once per
    /// process, never copied into the keychain or the Wino store).
    /// </summary>
    protected override ISet<TrustAnchor> GetTrustedAnchors()
    {
        var anchors = base.GetTrustedAnchors();
        MacSystemTrustAnchors.AddTo(anchors);
        return anchors;
    }
}
