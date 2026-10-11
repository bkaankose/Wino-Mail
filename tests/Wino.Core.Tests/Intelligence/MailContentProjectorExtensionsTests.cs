using FluentAssertions;
using MimeKit;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.ContentProcessing;
using Wino.Services.Extensions;
using Xunit;

namespace Wino.Core.Tests.Intelligence;

public class MailContentProjectorExtensionsTests
{
    [Fact]
    public void ProjectRenderedHtml_Should_Not_Count_Inline_Image_Data_Toward_Limit()
    {
        // Issue 1106: a short message with a ~2 MB inline JPEG renders as HTML over the
        // projector's 1,000,000 character limit once the cid: image becomes a data URL.
        var message = new MimeMessage();
        var related = new MultipartRelated
        {
            new TextPart("html") { Text = "<div><p>Meeting on Thursday</p><img src=\"cid:ii_photo\"></div>" },
            new MimePart("image", "jpeg")
            {
                ContentId = "ii_photo",
                Content = new MimeContent(new MemoryStream(new byte[2 * 1024 * 1024])),
                ContentTransferEncoding = ContentEncoding.Base64,
            },
        };
        message.Body = related;

        var visitor = new HtmlPreviewVisitor(Path.GetTempPath());
        message.Accept(visitor);
        var html = visitor.HtmlBody;

        html.Length.Should().BeGreaterThan(MailContentProjectorExtensions.MaxTextInputCharacters);
        MailContentProjectorExtensions.CanProjectRenderedHtml(html).Should().BeTrue();

        var projector = new MailContentProjector();
        var translation = projector.ProjectRenderedHtml(html, MailContentProjectionProfile.Translation);
        var inference = projector.ProjectRenderedHtml(html, MailContentProjectionProfile.Inference);

        inference.Projection.NormalizedText.Should().Contain("Meeting on Thursday");
        translation.Projection.NormalizedText.Should().Contain("Meeting on Thursday");
    }

    [Fact]
    public void RemoveEmbeddedImageData_Should_Strip_Data_Urls_And_Keep_Text()
    {
        var html = "<p>Hello</p><img src=\"data:image/jpeg;base64,/9j/4AAQSkZJRg==\"><div style=\"background:url(data:image/png;base64,iVBORw0KGgo=)\">World</div><img src=\"https://example.com/a.png\">";

        var stripped = MailContentProjectorExtensions.RemoveEmbeddedImageData(html);

        stripped.Should().NotContain("base64");
        stripped.Should().Contain("<p>Hello</p>").And.Contain("World").And.Contain("https://example.com/a.png");
    }

    [Fact]
    public void CanProjectRenderedHtml_Should_Reject_Text_Over_Limit()
    {
        var html = $"<p>{new string('a', MailContentProjectorExtensions.MaxTextInputCharacters)}</p>";

        MailContentProjectorExtensions.CanProjectRenderedHtml(html).Should().BeFalse();
    }
}
