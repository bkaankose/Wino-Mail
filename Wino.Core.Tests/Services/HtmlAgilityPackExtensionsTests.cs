using FluentAssertions;
using HtmlAgilityPack;
using Wino.Services.Extensions;
using Xunit;

namespace Wino.Core.Tests.Services;

public class HtmlAgilityPackExtensionsTests
{
    [Fact]
    public void ClearImages_Should_Block_Remote_Image_References_But_Keep_Embedded_Ones()
    {
        // Arrange
        var document = new HtmlDocument();
        document.LoadHtml("""
            <html>
                <head>
                    <style>
                        .hero { background-image: url('https://tracker.example/bg.png'); color: red; }
                    </style>
                </head>
                <body background="https://tracker.example/body.png">
                    <img id="remote" src="https://tracker.example/pixel.png" />
                    <img id="embedded" src="data:image/png;base64,AAAA" />
                    <img id="responsive" srcset="https://tracker.example/1x.png 1x, data:image/png;base64,BBBB 2x" />
                    <div id="inline-style" style="background-image:url('https://tracker.example/inline.png');color:blue;">hello</div>
                    <v:fill id="vml" src="https://tracker.example/vml.png"></v:fill>
                    <svg>
                        <image id="svg-remote" href="https://tracker.example/vector.svg"></image>
                        <use id="svg-local" href="#icon"></use>
                    </svg>
                </body>
            </html>
            """);

        // Act
        document.ClearImages();
        var output = document.DocumentNode.OuterHtml;

        // Assert
        output.Should().Contain("id=\"embedded\" src=\"data:image/png;base64,AAAA\"", "embedded inline images should still render");
        output.Should().NotContain("id=\"remote\" src=", "remote img sources should be removed");
        output.Should().NotContain("background=\"https://tracker.example/body.png\"", "background attributes can be used as trackers");
        output.Should().NotContain("srcset=", "responsive image candidates should be removed because they may fetch remote trackers");
        output.Should().NotContain("https://tracker.example/inline.png", "inline CSS should not be allowed to fetch remote images");
        output.Should().Contain("color:blue", "non-image inline styling should be preserved");
        output.Should().NotContain("https://tracker.example/bg.png", "style blocks should not be allowed to fetch remote images");
        output.Should().Contain("color: red", "safe CSS declarations should remain");
        output.Should().NotContain("id=\"vml\" src=", "VML image references should be removed");
        output.Should().NotContain("id=\"svg-remote\" href=", "SVG image references should not fetch remote content");
        output.Should().Contain("id=\"svg-local\" href=\"#icon\"", "local fragment references should remain");
    }
}
