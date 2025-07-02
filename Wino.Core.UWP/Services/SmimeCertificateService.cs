using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.UWP.Services;

public class SmimeCertificateService : ISmimeCertificateService
{
    private const string CertificateFriendlyName = "Wino Mail Certificate";

    /// <summary>
    /// Retrieves all personal certificates from the current user's certificate store.
    /// </summary>
    /// <remarks>This method enumerates certificates in the current user's "My" certificate store that have a
    /// private key and at least one extension. The store is opened in read-only mode.</remarks>
    /// <returns>An enumerable collection of <see cref="X509Certificate2"/> objects representing the personal certificates that
    /// meet the specified criteria. If no matching certificates are found, the collection will be empty.</returns>
    public IEnumerable<X509Certificate2> GetCertificates(StoreName storeName = StoreName.My, StoreLocation storeLocation = StoreLocation.CurrentUser, string emailAddress = null)
    {
        using var store = new X509Store(storeName, storeLocation);
        store.Open(OpenFlags.ReadOnly);
        var certs = store.Certificates.Where(cert => cert.FriendlyName == CertificateFriendlyName);
        return emailAddress != null ? certs.Where(cert => cert.Subject.Contains(emailAddress, StringComparison.OrdinalIgnoreCase)) : certs;
    }


    public void ImportCertificate(string fileExtension, byte[] rawData, string password = null, StoreName storeName = StoreName.My, StoreLocation storeLocation = StoreLocation.CurrentUser)
    {
        X509Certificate2Collection collection = [];

        if (fileExtension is ".p12" or ".pfx")
        {
            collection.AddRange(X509CertificateLoader.LoadPkcs12Collection(rawData, password, X509KeyStorageFlags.DefaultKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable));
        } else
        {
            collection.Add(X509CertificateLoader.LoadCertificate(rawData));
        }

        foreach (var cert in collection)
        {
            cert.FriendlyName = CertificateFriendlyName;
        }

        using var store = new X509Store(storeName, storeLocation);
        store.Open(OpenFlags.ReadWrite);
        store.AddRange(collection);
        store.Close();
    }

    public void RemoveCertificate(string thumbprint, StoreName storeName = StoreName.My, StoreLocation storeLocation = StoreLocation.CurrentUser)
    {
        using var store = new X509Store(storeName, storeLocation);
        store.Open(OpenFlags.ReadWrite);
        var cert = store.Certificates.FirstOrDefault(c => c.Thumbprint == thumbprint);
        if (cert != null)
        {
            store.Remove(cert);
        }
    }
}
