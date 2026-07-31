namespace Wino.Core.Domain.Exceptions;

public class ImapTestSSLCertificateException : System.Exception
{
    public ImapTestSSLCertificateException(string issuer, string expirationDateString, string validFromDateString)
    {
        Issuer = issuer;
        ExpirationDateString = expirationDateString;
        ValidFromDateString = validFromDateString;
    }

    public string Issuer { get; set; }
    public string ExpirationDateString { get; set; }
    public string ValidFromDateString { get; set; }

}
