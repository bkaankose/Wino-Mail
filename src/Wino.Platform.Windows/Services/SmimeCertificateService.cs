using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Runtime.Versioning;
using MimeKit.Cryptography;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Platform.Windows.Services;

[SupportedOSPlatform("windows")]
public class SmimeCertificateService : ISmimeCertificateService
{
    private const string CertificateFriendlyName = "Wino Mail Certificate";

    public SecureMimeContext CreateContext(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new WindowsSecureMimeContext();
    }

    public IReadOnlyList<X509Certificate2> GetCertificates(SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, string? emailAddress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = new X509Store(GetStoreName(purpose), StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var results = new List<X509Certificate2>();
        foreach (var certificate in store.Certificates)
        {
            if (certificate.FriendlyName == CertificateFriendlyName &&
                (emailAddress is null || certificate.Subject.Contains(emailAddress, StringComparison.OrdinalIgnoreCase)))
                results.Add(certificate);
            else
                certificate.Dispose();
        }
        return results;
    }

    public void ImportCertificate(string fileExtension, byte[] rawData, string? password = null, SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        X509Certificate2Collection collection = [];
        try
        {
            if (fileExtension is ".p12" or ".pfx")
                collection.AddRange(X509CertificateLoader.LoadPkcs12Collection(rawData, password,
                    X509KeyStorageFlags.DefaultKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable));
            else
                collection.Add(X509CertificateLoader.LoadCertificate(rawData));

            foreach (var cert in collection) cert.FriendlyName = CertificateFriendlyName;
            using var store = new X509Store(GetStoreName(purpose), StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.AddRange(collection);
        }
        finally
        {
            foreach (var cert in collection) cert.Dispose();
        }
    }

    public void RemoveCertificate(string thumbprint, SmimeCertificatePurpose purpose = SmimeCertificatePurpose.Personal, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var store = new X509Store(GetStoreName(purpose), StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        var certificates = store.Certificates;
        try
        {
            var certificate = certificates.FirstOrDefault(c => c.Thumbprint == thumbprint);
            if (certificate is not null) store.Remove(certificate);
        }
        finally
        {
            foreach (var certificate in certificates) certificate.Dispose();
        }
    }

    private static StoreName GetStoreName(SmimeCertificatePurpose purpose) => purpose switch
    {
        SmimeCertificatePurpose.Personal => StoreName.My,
        SmimeCertificatePurpose.Recipient => StoreName.AddressBook,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose))
    };
}
