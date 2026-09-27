using System.Threading;
using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.ContentProcessing;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Common;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Intelligence;

public class ReaderRewriteTests
{
    // Mirrors AiEmail:AllowedRewriteModes in the Wino Mail API (appsettings.json and
    // AiEmailOptions). A reader mode outside this list fails with validation_failed.
    private static readonly string[] ApiAllowedRewriteModes = ["polite", "angry", "formal", "friendly", "shorter", "clearer", "happy"];

    [Fact]
    public void GetReaderRewriteModeOptions_OnlyOffersModesTheApiAccepts()
    {
        var modes = AiActionCatalog.GetReaderRewriteModeOptions().Select(x => x.Mode).ToArray();

        modes.Should().Equal("clearer", "shorter", "formal", "friendly", "polite", "happy");
        modes.Should().OnlyContain(mode => ApiAllowedRewriteModes.Contains(mode));
    }

    [Fact]
    public void GetRewriteModeOptions_TheComposerCatalog_MatchesTheApiListExactly()
    {
        AiActionCatalog.GetRewriteModeOptions().Select(x => x.Mode).Should().BeEquivalentTo(ApiAllowedRewriteModes);
    }

    [Fact]
    public void GetReaderRewriteModeOptions_KeepsCatalogLabelsAndDescriptions()
    {
        var catalog = AiActionCatalog.GetRewriteModeOptions().ToDictionary(x => x.Mode);

        foreach (var option in AiActionCatalog.GetReaderRewriteModeOptions())
        {
            option.Should().Be(catalog[option.Mode]);
        }
    }

    [Fact]
    public void BuildRequestHtml_SendsOnlyCurrentMessageParagraphs_Encoded()
    {
        var html = ReaderRewriteContent.BuildRequestHtml(
        [
            Segment("s000001", MailContentSection.CurrentMessage, "Hi <team> & friends"),
            Segment("s000002", MailContentSection.QuotedHistory, "Older message"),
            Segment("s000003", MailContentSection.CurrentMessage, "Line one\nLine two"),
        ]);

        html.Should().Be("<p>Hi &lt;team&gt; &amp; friends</p><p>Line one<br>Line two</p>");
    }

    [Fact]
    public void BuildRequestHtml_FallsBackToAllSegments_WhenNoneIsCurrentMessage()
    {
        var html = ReaderRewriteContent.BuildRequestHtml(
        [
            Segment("s000001", MailContentSection.QuotedHistory, "Only quoted text"),
        ]);

        html.Should().Be("<p>Only quoted text</p>");
    }

    [Fact]
    public void BuildRequestHtml_RemovesProtectedProjectionMarkers()
    {
        var html = ReaderRewriteContent.BuildRequestHtml(
        [
            Segment("s000001", MailContentSection.CurrentMessage, "Call ⟦i1⟧Anna⟦/i1⟧ today ⟦p2⟧"),
        ]);

        html.Should().Be("<p>Call Anna today</p>");
    }

    [Fact]
    public void BuildRequestHtml_StopsBeforeTheBudget()
    {
        var html = ReaderRewriteContent.BuildRequestHtml(
        [
            Segment("s000001", MailContentSection.CurrentMessage, new string('a', 20)),
            Segment("s000002", MailContentSection.CurrentMessage, new string('b', 20)),
        ], maximumHtmlLength: 40);

        html.Should().Be("<p>" + new string('a', 20) + "</p>");
        html.Length.Should().BeLessThanOrEqualTo(40);
    }

    [Fact]
    public void BuildRequestHtml_ReturnsEmpty_ForMissingContent()
    {
        ReaderRewriteContent.BuildRequestHtml(null).Should().BeEmpty();
        ReaderRewriteContent.BuildRequestHtml([Segment("s000001", MailContentSection.CurrentMessage, "  ")]).Should().BeEmpty();
    }

    [Fact]
    public void ToPlainText_KeepsParagraphsAndDecodesEntities()
    {
        var text = ReaderRewriteContent.ToPlainText("<p>Hello &amp; welcome,</p><p>First<br/>Second</p><ul><li>One</li></ul>");

        text.Should().Be("Hello & welcome,\n\nFirst\nSecond\n\nOne");
    }

    [Fact]
    public void ToPlainText_ReturnsEmpty_ForBlankHtml()
    {
        ReaderRewriteContent.ToPlainText(null).Should().BeEmpty();
        ReaderRewriteContent.ToPlainText("   ").Should().BeEmpty();
    }

    [Fact]
    public void Snapshot_RewriteAvailability_FollowsSummarizeAndTranslateGate()
    {
        var eligible = new WinoIntelligenceSnapshot(true, true, true, false, MailMessageIntelligenceState.Unsupported, null, null, null, null);
        var noConsentOrQuota = eligible with { IsSummaryAvailable = false, IsTranslateAvailable = false };

        eligible.IsRewriteAvailable.Should().BeTrue();
        noConsentOrQuota.IsRewriteAvailable.Should().BeFalse();
        WinoIntelligenceSnapshot.Hidden.IsRewriteAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task RewriteAsync_WithoutWinoIntelligenceAccess_FailsWithoutCallingTheApi()
    {
        var profileService = new Mock<IWinoAccountProfileService>();
        profileService.Setup(x => x.GetActiveAccountAsync()).ReturnsAsync((WinoAccount?)null);

        using var coordinator = CreateCoordinator(profileService);
        var context = CreateContext();

        var result = await coordinator.RewriteAsync(context, Guid.NewGuid(), "clearer");

        result.IsSuccess.Should().BeFalse();
        result.IsCanceled.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error.Should().Be(WinoAccountApiErrorTranslator.Translate(WinoAccountApiErrorTranslator.IntelligenceConsentRequiredCode));
        result.ContentKey.Should().Be(context.ContentKey);
        profileService.Verify(
            x => x.RewriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task IsDraftRewriteAvailableAsync_FollowsAiPackConsentAndQuota()
    {
        var localAccountId = Guid.NewGuid();
        var (coordinator, _, store) = CreateEligibleCoordinator(localAccountId);
        using (coordinator)
        {
            (await coordinator.IsDraftRewriteAvailableAsync(localAccountId)).Should().BeTrue();

            store.Setup(x => x.GetAccessAsync(localAccountId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(((Guid, bool, bool)?)(Guid.NewGuid(), true, false));
            (await coordinator.IsDraftRewriteAvailableAsync(localAccountId)).Should().BeFalse("consent is required");
        }
    }

    [Fact]
    public async Task RewriteDraftAsync_SendsTheWholeDraftAsComposing()
    {
        var localAccountId = Guid.NewGuid();
        var (coordinator, profileService, _) = CreateEligibleCoordinator(localAccountId);
        profileService
            .Setup(x => x.RewriteAsync("<p>draft</p>", "happy", RewriteContexts.Composing, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ApiEnvelope<AiTextResultDto>.Success(new AiTextResultDto("<p>Draft!</p>")));

        using (coordinator)
        {
            var result = await coordinator.RewriteDraftAsync(localAccountId, Guid.NewGuid(), "<p>draft</p>", "happy");

            result.IsSuccess.Should().BeTrue();
            result.Value.Should().Be("<p>Draft!</p>");
            result.ContentKey.Should().Be(WinoIntelligenceCoordinator.CreateDraftContentKey(localAccountId));
        }
    }

    [Fact]
    public async Task RewriteDraftAsync_RejectsAnOversizedDraft_WithoutCallingTheApi()
    {
        var localAccountId = Guid.NewGuid();
        var (coordinator, profileService, _) = CreateEligibleCoordinator(localAccountId);
        var html = "<p>" + new string('a', ReaderRewriteContent.ApiMaximumHtmlLength) + "</p>";

        using (coordinator)
        {
            var result = await coordinator.RewriteDraftAsync(localAccountId, Guid.NewGuid(), html, "formal");

            result.IsSuccess.Should().BeFalse();
            result.Error.Should().Be(WinoAccountApiErrorTranslator.Translate(ApiErrorCodes.AiHtmlTooLarge));
            profileService.Verify(
                x => x.RewriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
    }

    [Fact]
    public async Task RewriteDraftAsync_WithoutAccess_FailsWithoutCallingTheApi()
    {
        var profileService = new Mock<IWinoAccountProfileService>();
        profileService.Setup(x => x.GetActiveAccountAsync()).ReturnsAsync((WinoAccount?)null);

        using var coordinator = CreateCoordinator(profileService);
        var result = await coordinator.RewriteDraftAsync(Guid.NewGuid(), Guid.NewGuid(), "<p>draft</p>", "formal");

        result.Error.Should().Be(WinoAccountApiErrorTranslator.Translate(WinoAccountApiErrorTranslator.IntelligenceConsentRequiredCode));
        profileService.Verify(
            x => x.RewriteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// With no account snapshot service the coordinator trusts the local access record, which it
    /// stores without a Wino account id; an account with an empty id therefore matches it.
    /// </summary>
    private static (WinoIntelligenceCoordinator Coordinator, Mock<IWinoAccountProfileService> Profile, Mock<IMailIntelligenceStore> Store) CreateEligibleCoordinator(Guid localAccountId)
    {
        var profileService = new Mock<IWinoAccountProfileService>();
        profileService.Setup(x => x.GetActiveAccountAsync()).ReturnsAsync(new WinoAccount { Id = Guid.Empty });
        var store = new Mock<IMailIntelligenceStore>();
        store.Setup(x => x.GetAccessAsync(localAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((Guid, bool, bool)?)(Guid.NewGuid(), true, true));

        var coordinator = new WinoIntelligenceCoordinator(
            profileService.Object,
            Mock.Of<IWinoAccountApiClient>(),
            Mock.Of<IMailIntelligenceCoordinator>(),
            Mock.Of<IIntelligenceMessageContextResolver>(),
            store.Object,
            Mock.Of<IMimeFileService>(),
            Mock.Of<IMailService>(),
            Mock.Of<IAccountService>(),
            Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<ITranslationService>(),
            Mock.Of<IPreferencesService>(),
            Mock.Of<IWinoLogger>(),
            Mock.Of<IMailContentProjector>());
        return (coordinator, profileService, store);
    }

    private static MailContentSegment Segment(string id, MailContentSection section, string text)
        => new(id, section, MailContentSegmentKind.Paragraph, text);

    private static WinoIntelligenceContext CreateContext()
        => new(
            "account:message",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "message-id",
            "user@example.com",
            MailProviderType.Outlook,
            true,
            "Subject",
            "sender@example.com",
            DateTimeOffset.UtcNow,
            "<p>Hello</p>");

    private static WinoIntelligenceCoordinator CreateCoordinator(Mock<IWinoAccountProfileService> profileService)
        => new(
            profileService.Object,
            Mock.Of<IWinoAccountApiClient>(),
            Mock.Of<IMailIntelligenceCoordinator>(),
            Mock.Of<IIntelligenceMessageContextResolver>(),
            Mock.Of<IMailIntelligenceStore>(),
            Mock.Of<IMimeFileService>(),
            Mock.Of<IMailService>(),
            Mock.Of<IAccountService>(),
            Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<ITranslationService>(),
            Mock.Of<IPreferencesService>(),
            Mock.Of<IWinoLogger>(),
            Mock.Of<IMailContentProjector>());
}
