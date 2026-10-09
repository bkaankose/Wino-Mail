using System.Runtime.InteropServices;
using Org.BouncyCastle.Pkix;
using Org.BouncyCastle.X509;
using Serilog;

namespace Wino.Platform.MacOS.Services;

/// <summary>
/// The macOS system root certificates, read once per process from the Security framework's default anchor
/// set (<c>SecTrustCopyAnchorCertificates</c>). This reads the built-in trust store only; it never writes to,
/// or prompts for, the user's keychain.
/// </summary>
internal static class MacSystemTrustAnchors
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";

    private static readonly Lazy<IReadOnlyList<X509Certificate>> Roots = new(LoadRoots, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<X509Certificate> Certificates => Roots.Value;

    /// <summary>Adds a trust anchor for every macOS system root not already in <paramref name="anchors"/>.</summary>
    public static void AddTo(ISet<TrustAnchor> anchors)
    {
        foreach (var certificate in Certificates)
            anchors.Add(new TrustAnchor(certificate, null));
    }

    private static IReadOnlyList<X509Certificate> LoadRoots()
    {
        var result = new List<X509Certificate>();
        nint array = 0;

        try
        {
            var status = SecTrustCopyAnchorCertificates(out array);
            if (status != 0 || array == 0)
            {
                Log.Warning("macOS system anchor certificates could not be read (OSStatus {Status}).", status);
                return result;
            }

            var parser = new X509CertificateParser();
            var count = CFArrayGetCount(array);

            for (nint i = 0; i < count; i++)
            {
                var certificate = CFArrayGetValueAtIndex(array, i);
                if (certificate == 0)
                    continue;

                var der = CopyDer(certificate);
                if (der is null)
                    continue;

                try
                {
                    result.Add(parser.ReadCertificate(der));
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Skipped an unreadable macOS system anchor certificate.");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "macOS system anchor certificates could not be loaded.");
        }
        finally
        {
            if (array != 0) CFRelease(array);
        }

        return result;
    }

    private static byte[]? CopyDer(nint certificate)
    {
        var data = SecCertificateCopyData(certificate);
        if (data == 0)
            return null;

        try
        {
            var length = (int)CFDataGetLength(data);
            var bytes = new byte[length];
            Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, length);
            return bytes;
        }
        finally
        {
            CFRelease(data);
        }
    }

    [DllImport(Security)]
    private static extern int SecTrustCopyAnchorCertificates(out nint anchors);
    [DllImport(Security)]
    private static extern nint SecCertificateCopyData(nint certificate);
    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetCount(nint array);
    [DllImport(CoreFoundation)]
    private static extern nint CFArrayGetValueAtIndex(nint array, nint index);
    [DllImport(CoreFoundation)]
    private static extern nint CFDataGetLength(nint data);
    [DllImport(CoreFoundation)]
    private static extern nint CFDataGetBytePtr(nint data);
    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint value);
}
