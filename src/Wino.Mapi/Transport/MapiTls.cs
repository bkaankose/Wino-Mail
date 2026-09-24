using System;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Wino.Mapi.Transport;

/// <summary>
/// A seam for the host application to take part in server certificate validation. Windows completes
/// an incomplete chain by itself, fetching missing issuers; not every platform does, so a head that
/// runs somewhere stricter can set a validator that handles it. Unset - which is the default, and
/// what the desktop app leaves it as - the platform's own validation applies unchanged.
/// </summary>
public static class MapiTls
{
    public static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>? ServerCertificateValidation { get; set; }

    internal static void Apply(HttpClientHandler handler)
    {
        if (ServerCertificateValidation is { } validate)
            handler.ServerCertificateCustomValidationCallback = validate;
    }
}
