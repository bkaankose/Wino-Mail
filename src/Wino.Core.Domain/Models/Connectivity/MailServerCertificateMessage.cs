namespace Wino.Core.Domain.Models.Connectivity;

/// <summary>The text of the certificate-trust prompt, shared by every place that tests a mail server.</summary>
public static class MailServerCertificateMessage
{
    public static string Build(MailServerCertificateFailure failure)
        => $"{Translator.IMAPSetupDialog_CertificateAllowanceRequired_Row0}\n\n" +
           $"{Translator.IMAPSetupDialog_CertificateProtocol}: {failure.Protocol}\n{Translator.IMAPSetupDialog_CertificateEndpoint}: {failure.Host}:{failure.Port}\n" +
           $"{Translator.IMAPSetupDialog_CertificateSubject}: {failure.Subject}\n{Translator.IMAPSetupDialog_CertificateSans}: {failure.SubjectAlternativeNames}\n" +
           $"{Translator.IMAPSetupDialog_CertificateIssuer}: {failure.Issuer}\n" +
           $"{Translator.IMAPSetupDialog_CertificateValidFrom}: {failure.ValidFromUtc:u}\n" +
           $"{Translator.IMAPSetupDialog_CertificateValidTo}: {failure.ValidToUtc:u}\n" +
           $"{Translator.IMAPSetupDialog_CertificateFingerprint}: {failure.CertificateSha256}\n" +
           $"{Translator.IMAPSetupDialog_CertificateFailureReason}: {failure.ChainStatusDetails}\n\n" +
           $"{Translator.IMAPSetupDialog_CertificateAllowanceRequired_Row1}";
}
