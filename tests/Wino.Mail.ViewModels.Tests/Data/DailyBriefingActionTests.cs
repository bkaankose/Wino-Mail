using System;
using FluentAssertions;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Xunit;

namespace Wino.Mail.ViewModels.Tests.Data;

/// <summary>
/// The card's command comes from Enrichment's typed smart actions, so these cover the mapping and
/// the fallback that reads a code from the body when no action carries one.
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

    [Fact]
    public void ForActions_ShouldPreferCopyingACode_OverTimeBoundActions()
    {
        MailSmartAction[] actions =
        [
            new PaymentDueAction("Due Friday", "Acme", "12.00", "EUR", new DateOnly(2026, 10, 2), null),
            new OneTimeCodeAction("Code 481223", "481223", null, null),
        ];

        DailyBriefingActionPresentationFactory.ForActions(actions).Execution
            .Should().Be(DailyBriefingActionExecution.CopyVerificationCode);
    }

    [Fact]
    public void ForActions_ShouldReply_WhenEnrichmentSuggestedAReply()
        => DailyBriefingActionPresentationFactory.ForActions(
                [new SuggestedReplyAction("Can you confirm?", "Yes, confirmed.", SuggestedReplyIntent.Accept)])
            .Execution.Should().Be(DailyBriefingActionExecution.Reply);

    [Fact]
    public void ForActions_ShouldOpen_WhenThereAreNoActionsOrOnlyAFollowUp()
    {
        DailyBriefingActionPresentationFactory.ForActions([]).Should().Be(DailyBriefingActionPresentationFactory.Open);
        DailyBriefingActionPresentationFactory.ForActions([new FollowUpAction("Waiting on them")])
            .Should().Be(DailyBriefingActionPresentationFactory.Open);
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
