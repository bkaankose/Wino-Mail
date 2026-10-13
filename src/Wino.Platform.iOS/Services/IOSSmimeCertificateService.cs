using System.Security.Cryptography.X509Certificates;
using MimeKit.Cryptography;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.iOS.Services;

/// <summary>
/// S/MIME is deferred on iOS (no system trust-anchor API to validate chains against). The platform capabilities
/// report <c>Smime: false</c>, so the UI does not offer it; calls that would need a store fail with
/// <see cref="PlatformNotSupportedException"/> as the interface documents, and listings are empty.
/// </summary>
public sealed class IOSSmimeCertificateService : ISmimeCertificateService
{
    private const string Unavailable = "S/MIME is not available on iOS.";

    public SecureMimeContext CreateContext(CancellationToken cancellationToken = default)
        => throw new PlatformNotSupportedException(Unavailable);

    public IReadOnlyList<X509Certificate2> GetCertificates(SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, string? emailAddress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Array.Empty<X509Certificate2>();
    }

    public void ImportCertificate(string fileExtension, byte[] rawData, string? password = null, SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, CancellationToken cancellationToken = default)
        => throw new PlatformNotSupportedException(Unavailable);

    /// <summary>Nothing is ever stored, so there is nothing to remove.</summary>
    public void RemoveCertificate(string thumbprint, SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, CancellationToken cancellationToken = default)
        => cancellationToken.ThrowIfCancellationRequested();
}
