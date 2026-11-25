using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Pkix;
using Org.BouncyCastle.X509;
using BcCertificate = Org.BouncyCastle.X509.X509Certificate;

namespace Wino.Platform.Linux.Security;

/// <summary>
/// The system root certificates, read once per process from the machine Root store (on Linux .NET reads the OpenSSL CA
/// bundle of the distribution). Read only; nothing is written to any store.
/// </summary>
internal static class LinuxSystemTrustAnchors
{
    private static readonly Lazy<BcCertificate[]> Roots = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static int Count => Roots.Value.Length;

    public static void AddTo(ISet<TrustAnchor> anchors)
    {
        foreach (var certificate in Roots.Value) anchors.Add(new TrustAnchor(certificate, null));
    }

    private static BcCertificate[] Load()
    {
        var parser = new X509CertificateParser();
        var roots = new List<BcCertificate>();
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            foreach (var certificate in store.Certificates)
            {
                using (certificate)
                {
                    try { roots.Add(parser.ReadCertificate(certificate.RawData)); }
                    catch (Exception exception) { Serilog.Log.Debug(exception, "Skipped an unreadable system root certificate."); }
                }
            }
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(exception, "The system root certificates could not be read.");
        }
        return [.. roots];
    }
}
