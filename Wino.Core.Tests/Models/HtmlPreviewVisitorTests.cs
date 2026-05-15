using Xunit;
using FluentAssertions;
using MimeKit;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Tests.Models;

public class HtmlPreviewVisitorTests
{
    [Fact]
    public void HtmlPreviewVisitor_Should_Remove_Blocked_Tags_And_Event_Attributes()
    {
        // Arrange
        var html = """
            <html>
                <body onload="alert('x')">
                    <h1 onclick="evil()">hello</h1>
                    <link rel="stylesheet" href="https://tracker.example/mail.css" />
                    <script>alert('xss')</script>
                    <iframe src="https://malicious.example"></iframe>
                    <object data="https://malicious.example/file.swf"></object>
                    <img src="https://cdn.example/image.png" onerror="steal()" />
                </body>
            </html>
            """;

        var message = new MimeMessage();
        message.Body = new TextPart("html") { Text = html };

        var visitor = new HtmlPreviewVisitor(Path.GetTempPath());

        // Act
        message.Accept(visitor);
        var output = visitor.HtmlBody;

        // Assert
        output.Should().NotContain("<script", "script tags must be blocked in rendered html");
        output.Should().NotContain("alert('xss')", "script contents must be blocked in rendered html");
        output.Should().NotContain("<link", "external stylesheet tags must be blocked in rendered html");
        output.Should().NotContain("<iframe", "iframe tags must be blocked in rendered html");
        output.Should().NotContain("<object", "object tags must be blocked in rendered html");
        output.Should().NotContain("onload=", "event handler attributes must be stripped");
        output.Should().NotContain("onclick=", "event handler attributes must be stripped");
        output.Should().NotContain("onerror=", "event handler attributes must be stripped");
        output.Should().Contain("oncontextmenu=\"return false;\"", "body context-menu suppression should be kept");
    }

    [Fact]
    public void HtmlPreviewVisitor_Should_Sanitize_Dangerous_Url_Attributes()
    {
        // Arrange
        var html = """
            <html>
                <body>
                    <a id="safe-link" href="https://contoso.com/path">safe</a>
                    <a id="js-link" href="javascript:alert('xss')">bad</a>
                    <img id="svg-script" src="data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==" />
                    <img id="allowed" src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAAB" />
                </body>
            </html>
            """;

        var message = new MimeMessage();
        message.Body = new TextPart("html") { Text = html };

        var visitor = new HtmlPreviewVisitor(Path.GetTempPath());

        // Act
        message.Accept(visitor);
        var output = visitor.HtmlBody;

        // Assert
        output.Should().Contain("id=\"safe-link\" href=\"https://contoso.com/path\"", "http/https links should be preserved");
        output.Should().Contain("id=\"js-link\"", "the element should remain");
        output.Should().NotContain("href=\"javascript:", "javascript URLs must be removed");
        output.Should().Contain("id=\"allowed\" src=\"data:image/png;base64", "safe image data URLs should be preserved");
        output.Should().NotContain("id=\"svg-script\" src=\"data:text/html", "non-image data URLs should be removed");
    }

    [Fact]
    public void HtmlPreviewVisitor_Should_Remove_JsonLd_Script_Blocks()
    {
        // Arrange
        var html = """
            <html>
                <body>
                    <script type="application/ld+json; charset=utf-8">
                    {
                        "@context": "https://schema.org",
                        "@type": "EmailMessage",
                        "description": "Structured metadata"
                    }
                    </script>
                    <script type="text/javascript">alert('xss')</script>
                </body>
            </html>
            """;

        var message = new MimeMessage();
        message.Body = new TextPart("html") { Text = html };

        var visitor = new HtmlPreviewVisitor(Path.GetTempPath());

        // Act
        message.Accept(visitor);
        var output = visitor.HtmlBody;

        // Assert
        output.Should().NotContain("<script", "all scripts should be blocked in rendered mail html");
        output.Should().NotContain("\"@context\": \"https://schema.org\"", "JSON-LD metadata should be removed instead of rendered as visible JSON");
        output.Should().NotContain("Structured metadata", "JSON-LD content should not remain in the rendered mail html");
        output.Should().NotContain("<script type=\"text/javascript\">", "executable scripts must still be blocked");
    }
}
