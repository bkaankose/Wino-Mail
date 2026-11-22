using System.Security.Cryptography.X509Certificates;
using MimeKit.Cryptography;
using Wino.Core.Domain.Enums;
using Wino.Services.Interfaces;

namespace Wino.Platform.Linux.Security;

/// <summary>
/// S/MIME certificates in the per-user .NET certificate store (owner decision for issue #1216), the Windows service's
/// logic. Linux has no certificate friendly names, so Wino keeps its certificates in stores of its own name instead of
/// My and AddressBook. Risk: .NET keeps these stores as files under <c>~/.dotnet/corefx/cryptography/x509stores</c>,
/// and a personal certificate's private key is protected by the file's permissions (0600) only, not by the keyring.
/// </summary>
public sealed class LinuxSmimeCertificateService : ISmimeCertificateService
{
    public const string PersonalStoreName = "wino-mail-personal";
    public const string RecipientStoreName = "wino-mail-recipient";

    public SecureMimeContext CreateContext(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var certificates = new List<X509Certificate2>();
        try
        {
            certificates.AddRange(GetCertificates(SmimeCertificatePurpose.Personal, null, cancellationToken));
            certificates.AddRange(GetCertificates(SmimeCertificatePurpose.Recipient, null, cancellationToken));
            return new LinuxSecureMimeContext(certificates, cancellationToken);
        }
        finally
        {
            foreach (var certificate in certificates) certificate.Dispose();
        }
    }

    public IReadOnlyList<X509Certificate2> GetCertificates(SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, string? emailAddress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = new X509Store(StoreFor(purpose), StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var results = new List<X509Certificate2>();
        foreach (var certificate in store.Certificates)
        {
            if (emailAddress is null || Matches(certificate, emailAddress)) results.Add(certificate);
            else certificate.Dispose();
        }
        return results;
    }

    public void ImportCertificate(string fileExtension, byte[] rawData, string? password = null, SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rawData);
        cancellationToken.ThrowIfCancellationRequested();
        var extension = (fileExtension ?? string.Empty).Trim().ToLowerInvariant();
        if (extension.Length > 0 && extension[0] != '.') extension = "." + extension;

        X509Certificate2Collection collection = [];
        try
        {
            if (extension is ".p12" or ".pfx")
                collection.AddRange(X509CertificateLoader.LoadPkcs12Collection(rawData, password, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable));
            else if (System.Text.Encoding.ASCII.GetString(rawData).Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
                collection.ImportFromPem(System.Text.Encoding.ASCII.GetString(rawData));
            else
                collection.Add(X509CertificateLoader.LoadCertificate(rawData));
            if (collection.Count == 0) throw new InvalidDataException("The file contains no certificate.");

            using var store = new X509Store(StoreFor(purpose), StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.AddRange(collection);
        }
        finally
        {
            foreach (var certificate in collection) certificate.Dispose();
        }
    }

    public void RemoveCertificate(string thumbprint, SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = new X509Store(StoreFor(purpose), StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var certificates = store.Certificates;
        try
        {
            var certificate = certificates.FirstOrDefault(candidate => string.Equals(candidate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
            if (certificate is not null) store.Remove(certificate);
        }
        finally
        {
            foreach (var certificate in certificates) certificate.Dispose();
        }
    }

    public static string StoreFor(SmimeCertificatePurpose purpose) => purpose switch
    {
        SmimeCertificatePurpose.Personal => PersonalStoreName,
        SmimeCertificatePurpose.Recipient => RecipientStoreName,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    /// <summary>The subject e-mail or the first rfc822 alternative name first, then any mention in the subject (Windows).</summary>
    private static bool Matches(X509Certificate2 certificate, string emailAddress)
    {
        var address = emailAddress.Trim();
        if (address.Length == 0) return true;
        if (string.Equals(certificate.GetNameInfo(X509NameType.EmailName, false), address, StringComparison.OrdinalIgnoreCase)) return true;
        return certificate.Subject.Contains(address, StringComparison.OrdinalIgnoreCase);
    }
}
