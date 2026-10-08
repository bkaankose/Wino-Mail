using MimeKit;
using MimeKit.Cryptography;
using Moq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Models;

public sealed class SmimeContextBoundaryTests
{
    [Fact]
    public void PortableDefaultPreservesDetachedClearBodyAndRecordsUnavailableVerification()
    {
        var signed = new MultipartSigned();
        signed.ContentType.Parameters["protocol"] = "application/pkcs7-signature";
        signed.Add(new TextPart("plain") { Text = "Clear body remains readable" });
        signed.Add(new MimePart("application", "pkcs7-signature"));
        var visitor = new HtmlPreviewVisitor();
        signed.Accept(visitor);

        Assert.Contains("Clear body remains readable", visitor.HtmlBody);
        Assert.IsType<PlatformNotSupportedException>(Assert.Single(visitor.CryptographyErrors));
        Assert.Empty(visitor.Signatures);
    }

    [Fact]
    public void MimeServiceDefersContextCreationUntilCryptographyIsActuallyNeeded()
    {
        var certificates = new Mock<ISmimeCertificateService>(MockBehavior.Strict);
        certificates.Setup(service => service.CreateContext(It.IsAny<CancellationToken>()))
            .Throws(new PlatformNotSupportedException("S/MIME is unavailable."));
        var service = new MimeFileService(Mock.Of<IApplicationConfiguration>(), certificates.Object);
        using var plain = new MimeMessage { Body = new TextPart("plain") { Text = "Ordinary mail" } };
        Assert.Contains("Ordinary mail", service.CreateHTMLPreviewVisitor(plain, string.Empty).HtmlBody);
        certificates.Verify(context => context.CreateContext(It.IsAny<CancellationToken>()), Times.Never);

        var signed = new MultipartSigned();
        signed.ContentType.Parameters["protocol"] = "application/pkcs7-signature";
        signed.Add(new TextPart("plain") { Text = "Signed clear body" });
        signed.Add(new MimePart("application", "pkcs7-signature"));
        using var message = new MimeMessage { Body = signed };
        var visitor = service.CreateHTMLPreviewVisitor(message, string.Empty);
        Assert.Contains("Signed clear body", visitor.HtmlBody);
        Assert.IsType<PlatformNotSupportedException>(Assert.Single(visitor.CryptographyErrors));
        certificates.Verify(context => context.CreateContext(It.IsAny<CancellationToken>()), Times.Once);
    }
}
