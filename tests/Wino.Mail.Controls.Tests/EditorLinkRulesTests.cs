using FluentAssertions;
using Wino.Editor;
using Xunit;

namespace Wino.Mail.Controls.Tests;

/// <summary>The link dialog rules the Mac, iOS and Android composers share.</summary>
public sealed class EditorLinkRulesTests
{
    [Fact]
    public void Kind_FollowsTheSelection()
    {
        EditorLinkRules.Kind(new EditorState()).Should().Be(EditorLinkDialogKind.InsertLink);
        EditorLinkRules.Kind(new EditorState { LinkUrl = "https://wino.mail" }).Should().Be(EditorLinkDialogKind.EditLink);
        EditorLinkRules.Kind(new EditorState { IsImageSelected = true, LinkUrl = "https://wino.mail" }).Should().Be(EditorLinkDialogKind.EditImageLink);
    }

    [Fact]
    public void InitialUrl_AndTextField_FollowTheSelection()
    {
        var image = new EditorState { IsImageSelected = true, ImageLinkUrl = "https://image", LinkUrl = "https://text" };
        EditorLinkRules.InitialUrl(image).Should().Be("https://image");
        EditorLinkRules.HasTextField(image).Should().BeFalse();
        EditorLinkRules.InitialUrl(new EditorState { LinkUrl = "https://text" }).Should().Be("https://text");
        EditorLinkRules.HasTextField(new EditorState()).Should().BeTrue();
    }

    [Theory]
    [InlineData(" wino.mail ", "https://wino.mail")]
    [InlineData("http://wino.mail", "http://wino.mail")]
    [InlineData("mailto:hello@wino.mail", "mailto:hello@wino.mail")]
    public void NormalizeUrl_AddsHttpsWithoutAScheme(string typed, string expected)
        => EditorLinkRules.NormalizeUrl(typed).Should().Be(expected);

    [Fact]
    public void CreateLink_NeedsAUrl_AndDropsTextForImages()
    {
        EditorLinkRules.CreateLink(new EditorState(), "  ", "text").Should().BeNull();
        EditorLinkRules.CreateLink(new EditorState(), "wino.mail", " Wino ").Should().Be(new EditorLinkCommandArgs("https://wino.mail", "Wino", true));
        EditorLinkRules.CreateLink(new EditorState(), "wino.mail", " ", false).Should().Be(new EditorLinkCommandArgs("https://wino.mail", null, false));
        EditorLinkRules.CreateLink(new EditorState { IsImageSelected = true }, "wino.mail", "ignored").Should().Be(new EditorLinkCommandArgs("https://wino.mail", null, true));
    }
}
