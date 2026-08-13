using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;

namespace Wino.Core.Domain.Interfaces;

public interface ISmimeCertificateService
{
    public IEnumerable<X509Certificate2> GetCertificates(StoreName storeName = StoreName.My, StoreLocation storeLocation = StoreLocation.CurrentUser, string emailAddress = null);
    public void ImportCertificate(string fileExtension, byte[] rawData, string password = null, StoreName storeName = StoreName.My, StoreLocation storeLocation = StoreLocation.CurrentUser);
    public void RemoveCertificate(string thumbprint, StoreName storeName = StoreName.My, StoreLocation storeLocation = StoreLocation.CurrentUser);
}
