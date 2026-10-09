using FluentAssertions;
using Wino.Core.Activation;
using Wino.Core.Domain.Enums;
using Xunit;

namespace Wino.Core.Tests.Activation;

public class ActivationUriClassifierTests
{
    [Theory]
    [InlineData("mailto:someone@example.com", ActivationUriKind.MailTo)]
    [InlineData("MAILTO:someone@example.com?subject=Hi", ActivationUriKind.MailTo)]
    [InlineData("wino://billing/success", ActivationUriKind.BillingSuccess)]
    [InlineData("webcal://example.com/calendar.ics", ActivationUriKind.Webcal)]
    [InlineData("webcals://example.com/calendar.ics", ActivationUriKind.Webcal)]
    [InlineData("file:///Users/test/Downloads/invite.ics", ActivationUriKind.CalendarFile)]
    [InlineData("file:///Users/test/Downloads/Card.VCF", ActivationUriKind.ContactFile)]
    [InlineData("/Users/test/Downloads/invite.ICS", ActivationUriKind.CalendarFile)]
    [InlineData("/Users/test/Downloads/contact.vcf", ActivationUriKind.ContactFile)]
    public void Classify_RecognizesSupportedActivations(string value, ActivationUriKind expected)
    {
        ActivationUriClassifier.Classify(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("wino://billing/success?session_id=secret")]
    [InlineData("wino://mail/open")]
    [InlineData("https://example.com/invite.ics")]
    [InlineData("/Users/test/Downloads/notes.txt")]
    [InlineData("not a url")]
    public void Classify_RejectsUnsupportedActivations(string? value)
    {
        ActivationUriClassifier.Classify(value).Should().Be(ActivationUriKind.Unsupported);
    }

    [Theory]
    [InlineData("file:///Users/test/Downloads/message.eml")]
    [InlineData("file:///Users/test/Downloads/My%20Message.EML")]
    [InlineData("/Users/test/Downloads/message.eml")]
    [InlineData("/Users/test/Downloads/MESSAGE.Eml")]
    public void Classify_RecognizesMailFiles(string value)
    {
        ActivationUriClassifier.Classify(value).Should().Be(ActivationUriKind.MailFile);
        ActivationUriClassifier.ClassifyFilePath(value.StartsWith('/') ? value : new Uri(value).LocalPath).Should().Be(ActivationUriKind.MailFile);
    }

    [Fact]
    public void Classify_NullUri_IsUnsupported()
    {
        ActivationUriClassifier.Classify((Uri?)null).Should().Be(ActivationUriKind.Unsupported);
    }

    [Theory]
    [InlineData(ActivationUriKind.MailTo, WinoApplicationMode.Mail)]
    [InlineData(ActivationUriKind.MailFile, WinoApplicationMode.Mail)]
    [InlineData(ActivationUriKind.Webcal, WinoApplicationMode.Calendar)]
    [InlineData(ActivationUriKind.CalendarFile, WinoApplicationMode.Calendar)]
    [InlineData(ActivationUriKind.ContactFile, WinoApplicationMode.Contacts)]
    [InlineData(ActivationUriKind.BillingSuccess, WinoApplicationMode.Settings)]
    public void GetMode_MapsKindsToModes(ActivationUriKind kind, WinoApplicationMode expected)
    {
        ActivationUriClassifier.GetMode(kind).Should().Be(expected);
    }

    [Fact]
    public void GetMode_Unsupported_IsNull()
    {
        ActivationUriClassifier.GetMode(ActivationUriKind.Unsupported).Should().BeNull();
    }
}
