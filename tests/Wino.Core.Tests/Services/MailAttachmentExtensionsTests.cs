using FluentAssertions;
using MailKit;
using MimeKit;
using Wino.Core.Domain.Entities.Mail;
using Wino.Services.Extensions;
using Xunit;

namespace Wino.Core.Tests.Services;

public class MailAttachmentExtensionsTests
{
    [Fact]
    public void HasMailAttachments_ShouldUseBodyStructure_WhenMimeIsNotDownloaded()
    {
        var summary = new MessageSummary(0)
        {
            UniqueId = new UniqueId(42),
            Body = new BodyPartMultipart(
                new ContentType("multipart", "mixed"),
                string.Empty,
                new BodyPartCollection
                {
                    new BodyPartText(new ContentType("text", "plain"), "1"),
                    new BodyPartBasic(new ContentType("application", "pdf"), "2")
                    {
                        ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
                    }
                })
        };

        summary.HasMailAttachments().Should().BeTrue();
        summary.GetMailDetails(new MailItemFolder { Id = Guid.NewGuid() }).HasAttachments.Should().BeTrue();
    }

    [Fact]
    public void MetadataAndMime_ShouldClassifyPdfAttachmentTheSameWay()
    {
        var summary = new MessageSummary(0)
        {
            Body = new BodyPartBasic(new ContentType("application", "pdf"), "1")
            {
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
            }
        };
        var pdf = new MimePart("application", "pdf")
        {
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            Content = new MimeContent(new MemoryStream([1, 2, 3]))
        };
        var message = new MimeMessage { Body = new Multipart("mixed") { new TextPart("plain") { Text = "Body" }, pdf } };

        summary.HasMailAttachments().Should().BeTrue();
        summary.HasMailAttachments(message).Should().BeTrue();
        message.GetMailAttachments().Should().ContainSingle().Which.Should().BeSameAs(pdf);
    }

    [Fact]
    public void SmimeSecurityParts_ShouldNotBeShownAsMailAttachments()
    {
        var signature = new MimePart("application", "pkcs7-signature")
        {
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            Content = new MimeContent(new MemoryStream([1, 2, 3]))
        };
        var message = new MimeMessage { Body = signature };

        message.GetMailAttachments().Should().BeEmpty();
    }
}
