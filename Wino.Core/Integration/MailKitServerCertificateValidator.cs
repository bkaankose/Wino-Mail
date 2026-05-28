using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Wino.Core.Domain.Exceptions;

namespace Wino.Core.Integration;

internal static class MailKitServerCertificateValidator
{
    public static bool Validate(X509Certificate certificate, SslPolicyErrors sslPolicyErrors, bool throwOnSslHandshake)
    {
        if (sslPolicyErrors == SslPolicyErrors.None)
            return true;

        if (throwOnSslHandshake)
        {
            throw new ImapTestSSLCertificateException(
                certificate?.Issuer ?? string.Empty,
                certificate?.GetExpirationDateString() ?? string.Empty,
                certificate?.GetEffectiveDateString() ?? string.Empty);
        }

        return true;
    }
}
