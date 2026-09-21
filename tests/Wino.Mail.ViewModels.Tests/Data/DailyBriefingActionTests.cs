using FluentAssertions;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests.Data;

/// <summary>
/// The card's command comes from the action Classification returns, so these cover the mapping and
/// the one piece of content the action needs but the artifact does not carry: the code itself.
/// </summary>
public class DailyBriefingActionTests
{
    [Fact]
    public void Create_ShouldReply_WhenClassificationAskedForReply()
    {
        var action = DailyBriefingActionPresentationFactory.Create("reply");

        action.Execution.Should().Be(DailyBriefingActionExecution.Reply);
        action.AutomationId.Should().Be("DailyBriefingReplyButton");
    }

    [Fact]
    public void Create_ShouldOpenSource_WhenReplyIsNotAllowed()
    {
        var action = DailyBriefingActionPresentationFactory.Create("reply", allowReply: false);

        action.Execution.Should().Be(DailyBriefingActionExecution.OpenSource);
    }

    [Theory]
    [InlineData("pay")]
    [InlineData("confirm")]
    [InlineData("addtocalendar")]
    [InlineData("trackshipment")]
    [InlineData("sign")]
    public void Create_ShouldOpenSourceWithItsOwnWording_ForActionsWinoCannotComplete(string action)
    {
        var presentation = DailyBriefingActionPresentationFactory.Create(action);

        presentation.Execution.Should().Be(DailyBriefingActionExecution.OpenSource);
        presentation.Label.Should().NotBe(DailyBriefingActionPresentationFactory.Open.Label);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("something-a-later-model-returns")]
    public void Create_ShouldFallBackToOpen_ForAnythingElse(string? action)
    {
        var presentation = DailyBriefingActionPresentationFactory.Create(action);

        presentation.Should().Be(DailyBriefingActionPresentationFactory.Open);
    }

    [Theory]
    [InlineData("Your verification code is 483920.", "483920")]
    [InlineData("284915 is your Wino security code", "284915")]
    [InlineData("Use one-time code AB4F92 to continue", "AB4F92")]
    [InlineData("Enter the code: 129-458 on the sign-in page", "129-458")]
    public void TryExtract_ShouldFindTheCode_WhenItSitsBesideCodeWording(string body, string expected)
        => VerificationCodeExtractor.TryExtract(body).Should().Be(expected);

    [Theory]
    [InlineData("Your order 483920 has shipped and will arrive Monday.")]
    [InlineData("")]
    [InlineData(null)]
    public void TryExtract_ShouldFindNothing_WhenNoCodeWordingIsPresent(string? body)
        => VerificationCodeExtractor.TryExtract(body).Should().BeNull();

    [Fact]
    public void TryExtract_ShouldIgnoreAYearNextToCodeWording()
        => VerificationCodeExtractor.TryExtract("Security notice 2026 about your account").Should().BeNull();

    [Fact]
    public void ToPlainText_ShouldDropMarkup_SoAnHtmlOnlyBodyCanBeRead()
    {
        var text = VerificationCodeExtractor.ToPlainText("<p>Your code is <b>771204</b></p>");

        VerificationCodeExtractor.TryExtract(text).Should().Be("771204");
    }
}
