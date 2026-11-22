using System.Security.Cryptography.X509Certificates;
using MimeKit.Cryptography;
using Org.BouncyCastle.Pkix;
using Org.BouncyCastle.X509;

namespace Wino.Platform.Linux.Security;

/// <summary>
/// The S/MIME context of one operation (macOS MacSecureMimeContext pattern): MimeKit's in-memory BouncyCastle context,
/// filled from the .NET store. A certificate with a private key travels as PKCS#12 under a one-time random password,
/// never written anywhere. MimeKit's WindowsSecureMimeContext works on Windows only.
/// </summary>
internal sealed class LinuxSecureMimeContext : TemporarySecureMimeContext
{
    public LinuxSecureMimeContext(IEnumerable<X509Certificate2> certificates, CancellationToken cancellationToken = default)
    {
        var parser = new X509CertificateParser();
        foreach (var certificate in certificates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (certificate.HasPrivateKey)
            {
                var password = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
                using var stream = new MemoryStream(certificate.Export(X509ContentType.Pkcs12, password), writable: false);
                Import(stream, password, cancellationToken);
            }
            else
            {
                Import(parser.ReadCertificate(certificate.RawData), cancellationToken);
            }
        }
    }

    /// <summary>The temporary context trusts only imported self-signed certificates; add the system roots so CA-issued signatures verify.</summary>
    protected override ISet<TrustAnchor> GetTrustedAnchors()
    {
        var anchors = base.GetTrustedAnchors();
        LinuxSystemTrustAnchors.AddTo(anchors);
        return anchors;
    }
}
