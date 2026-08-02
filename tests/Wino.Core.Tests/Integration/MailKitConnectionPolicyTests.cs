using FluentAssertions;
using MailKit.Security;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.AutoDiscovery;
using Wino.Core.Integration;
using Xunit;

namespace Wino.Core.Tests.Integration;

public class MailKitConnectionPolicyTests
{
    [Fact]
    public void CertificatePolicy_AllowsConsentOnlyForCurrentChainTrustFailures()
    {
        using var certificate = CreateSelfSignedCertificate(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(certificate).Should().BeFalse();

        var eligible = MailKitServerCertificateValidator.CreateFailure(
            certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors,
            MailServerProtocol.Imap, "mail.example.com", 993);
        var hostnameMismatch = MailKitServerCertificateValidator.CreateFailure(
            certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
            MailServerProtocol.Imap, "wrong.example.com", 993);

        eligible.CanTrust.Should().BeTrue();
        hostnameMismatch.CanTrust.Should().BeFalse();
    }

    [Fact]
    public void CertificateTrust_CannotBeReusedAcrossProtocols()
    {
        using var certificate = CreateSelfSignedCertificate(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.Build(certificate);
        var failure = MailKitServerCertificateValidator.CreateFailure(
            certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors,
            MailServerProtocol.Imap, "mail.example.com", 993);
        var trust = failure.CreateTrust(Guid.NewGuid());

        var action = () => MailKitServerCertificateValidator.Validate(
            certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors,
            MailServerProtocol.Smtp, "mail.example.com", 993, trust);

        action.Should().Throw<Wino.Core.Domain.Exceptions.MailServerCertificateException>();
    }

    [Theory]
    [InlineData(ImapConnectionSecurity.Auto, SecureSocketOptions.Auto)]
    [InlineData(ImapConnectionSecurity.None, SecureSocketOptions.None)]
    [InlineData(ImapConnectionSecurity.StartTls, SecureSocketOptions.StartTls)]
    [InlineData(ImapConnectionSecurity.SslTls, SecureSocketOptions.SslOnConnect)]
    public void CorrectedSmtpPolicy_MapsConfiguredTransportExactly(
        ImapConnectionSecurity configured,
        SecureSocketOptions expected)
    {
        var information = new CustomServerInformation
        {
            ConnectionPolicyVersion = ImapConnectionPolicyVersion.Corrected,
            OutgoingServerSocketOption = configured
        };

        MailKitSmtpConnectionPolicy.GetSocketOptions(information).Should().Be(expected);
    }

    [Theory]
    [InlineData(ImapConnectionSecurity.None)]
    [InlineData(ImapConnectionSecurity.StartTls)]
    [InlineData(ImapConnectionSecurity.SslTls)]
    public void LegacySmtpPolicy_AlwaysRetainsMailKitAuto(ImapConnectionSecurity configured)
    {
        var information = new CustomServerInformation
        {
            ConnectionPolicyVersion = ImapConnectionPolicyVersion.Legacy,
            OutgoingServerSocketOption = configured
        };

        MailKitSmtpConnectionPolicy.GetSocketOptions(information).Should().Be(SecureSocketOptions.Auto);
    }

    [Fact]
    public void Autodiscovery_PreservesTransportAndSupportedAuthentication()
    {
        var settings = CreateSettings(
            new AutoDiscoveryProviderSetting
            {
                Protocol = "IMAP",
                Address = "imap.example.com",
                Port = 993,
                Secure = "SSL",
                Username = "user",
                AuthenticationMethods = ["OAuth2", "password-cleartext"]
            },
            new AutoDiscoveryProviderSetting
            {
                Protocol = "SMTP",
                Address = "smtp.example.com",
                Port = 587,
                Secure = "STARTTLS",
                Username = "user@example.com",
                AuthenticationMethods = ["none"]
            });

        var result = settings.ToServerInformation();

        result.ConnectionPolicyVersion.Should().Be(ImapConnectionPolicyVersion.Corrected);
        result.IncomingServerSocketOption.Should().Be(ImapConnectionSecurity.SslTls);
        result.OutgoingServerSocketOption.Should().Be(ImapConnectionSecurity.StartTls);
        result.IncomingAuthenticationMethod.Should().Be(ImapAuthenticationMethod.NormalPassword);
        result.OutgoingAuthenticationMethod.Should().Be(ImapAuthenticationMethod.None);
    }

    [Fact]
    public void Autodiscovery_RejectsOAuthOnlyEndpoint()
    {
        var settings = CreateSettings(
            new AutoDiscoveryProviderSetting
            {
                Protocol = "IMAP",
                Address = "imap.example.com",
                Port = 993,
                Secure = "SSL",
                AuthenticationMethods = ["OAuth2"]
            },
            new AutoDiscoveryProviderSetting
            {
                Protocol = "SMTP",
                Address = "smtp.example.com",
                Port = 587,
                Secure = "STARTTLS",
                AuthenticationMethods = ["password-cleartext"]
            });

        var action = settings.ToServerInformation;

        action.Should().Throw<NotSupportedException>().WithMessage("*OAuth*");
    }

    private static AutoDiscoverySettings CreateSettings(params AutoDiscoveryProviderSetting[] providerSettings)
        => new()
        {
            UserMinimalSettings = new AutoDiscoveryMinimalSettings
            {
                Email = "user@example.com",
                DisplayName = "User",
                Password = "secret"
            },
            Settings = [.. providerSettings]
        };

    private static X509Certificate2 CreateSelfSignedCertificate(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=mail.example.com", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName("mail.example.com");
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        return request.CreateSelfSigned(notBefore, notAfter);
    }
}
