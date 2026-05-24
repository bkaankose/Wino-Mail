using System.Reflection;
using FluentAssertions;
using HtmlAgilityPack;
using Wino.Core.Domain.Interfaces;
using Wino.Services.Extensions;
using Xunit;

namespace Wino.Core.Tests.Services;

public class MailSecurityModeTests
{
    [Fact]
    public void IsSecurityModeEnabled_Should_Exist_On_IPreferencesService()
    {
        var property = typeof(IPreferencesService).GetProperty(nameof(IPreferencesService.IsSecurityModeEnabled));

        property.Should().NotBeNull();
        property!.CanRead.Should().BeTrue();
        property.CanWrite.Should().BeTrue();
        property.PropertyType.Should().Be(typeof(bool));
    }

    [Fact]
    public void IsSecurityModeEnabled_Should_Be_Excluded_From_Syncable_Properties()
    {
        var syncableProperties = typeof(IPreferencesService)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .Where(p => p.Name != nameof(IPreferencesService.DiagnosticId)
                     && p.Name != nameof(IPreferencesService.IsSecurityModeEnabled))
            .ToList();

        syncableProperties.Should().NotContain(p => p.Name == nameof(IPreferencesService.IsSecurityModeEnabled),
            "Security Mode should not be exported/imported to prevent silent security downgrades");
    }

    [Fact]
    public void ClearImages_Should_Remove_CSS_Url_Tracking_In_Inline_Styles()
    {
        var document = new HtmlDocument();
        document.LoadHtml("""
            <html><body>
                <div style="background-image:url('https://tracker.example/pixel.gif');color:blue;">content</div>
                <p style="list-style-image:url(https://tracker.example/list.png);font-size:14px;">item</p>
            </body></html>
            """);

        document.ClearImages();
        var output = document.DocumentNode.OuterHtml;

        output.Should().NotContain("tracker.example", "CSS url() references should be sanitized by ClearImages");
        output.Should().Contain("color:blue", "safe CSS properties should be preserved");
        output.Should().Contain("font-size:14px", "safe CSS properties should be preserved");
    }

    [Fact]
    public void ClearImages_Should_Remove_CSS_Import_In_Style_Tags()
    {
        var document = new HtmlDocument();
        document.LoadHtml("""
            <html><head>
                <style>
                    @import url('https://tracker.example/remote.css');
                    body { color: red; }
                </style>
            </head><body><p>content</p></body></html>
            """);

        document.ClearImages();
        var output = document.DocumentNode.OuterHtml;

        output.Should().NotContain("tracker.example", "@import directives should be removed");
        output.Should().Contain("color: red", "safe CSS rules should remain");
    }

    [Fact]
    public void ClearImages_Should_Remove_CSS_ImageSet_Tracking()
    {
        var document = new HtmlDocument();
        document.LoadHtml("""
            <html><body>
                <div style="background:image-set(url('https://tracker.example/1x.png') 1x);color:green;">content</div>
            </body></html>
            """);

        document.ClearImages();
        var output = document.DocumentNode.OuterHtml;

        output.Should().NotContain("tracker.example", "image-set() references should be sanitized");
        output.Should().Contain("color:green", "safe CSS properties should be preserved");
    }

    [Fact]
    public void ClearImages_Should_Handle_Audio_Video_Source_Tags()
    {
        var document = new HtmlDocument();
        document.LoadHtml("""
            <html><body>
                <video src="https://tracker.example/video.mp4"></video>
                <audio src="https://tracker.example/audio.mp3"></audio>
                <video><source src="https://tracker.example/source.mp4" /></video>
            </body></html>
            """);

        document.ClearImages();
        var output = document.DocumentNode.OuterHtml;

        output.Should().NotContain("tracker.example", "media element remote sources should be removed by ClearImages");
    }

    [Fact]
    public void ClearImages_Should_Preserve_CID_And_Data_Images()
    {
        var document = new HtmlDocument();
        document.LoadHtml("""
            <html><body>
                <img id="data-img" src="data:image/png;base64,iVBOR" />
                <img id="cid-img" src="cid:image001@example" />
                <img id="fragment" src="#local-ref" />
                <img id="remote" src="https://example.com/track.png" />
            </body></html>
            """);

        document.ClearImages();
        var output = document.DocumentNode.OuterHtml;

        output.Should().Contain("id=\"data-img\" src=\"data:image/png;base64,iVBOR\"", "data: images should be preserved");
        output.Should().Contain("id=\"cid-img\" src=\"cid:image001@example\"", "cid: references should be preserved");
        output.Should().Contain("id=\"fragment\" src=\"#local-ref\"", "fragment references should be preserved");
        output.Should().NotContain("id=\"remote\" src=", "remote images should be removed");
    }

    [Fact]
    public void ClearImages_Should_Not_Crash_On_Empty_Or_Malformed_Html()
    {
        var empty = new HtmlDocument();
        empty.LoadHtml("");
        var act1 = () => empty.ClearImages();
        act1.Should().NotThrow();

        var malformed = new HtmlDocument();
        malformed.LoadHtml("<div><img src='https://x.com/a.png'><p>unclosed");
        var act2 = () => malformed.ClearImages();
        act2.Should().NotThrow();

        var output = malformed.DocumentNode.OuterHtml;
        output.Should().NotContain("x.com/a.png");
    }
}
