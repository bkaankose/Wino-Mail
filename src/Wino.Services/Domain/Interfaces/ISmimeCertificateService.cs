using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Wino.Core.Domain.Enums;
using MimeKit.Cryptography;

namespace Wino.Core.Domain.Interfaces;

public interface ISmimeCertificateService
{
    /// <summary>Creates a caller-owned context for application S/MIME operations; unavailable implementations throw PlatformNotSupportedException.</summary>
    SecureMimeContext CreateContext(CancellationToken cancellationToken = default);
    /// <summary>Returns caller-owned certificates; dispose each instance after its final use.</summary>
    IReadOnlyList<X509Certificate2> GetCertificates(SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, string emailAddress = null, CancellationToken cancellationToken = default);
    void ImportCertificate(string fileExtension, byte[] rawData, string password = null, SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, CancellationToken cancellationToken = default);
    void RemoveCertificate(string thumbprint, SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, CancellationToken cancellationToken = default);
}
