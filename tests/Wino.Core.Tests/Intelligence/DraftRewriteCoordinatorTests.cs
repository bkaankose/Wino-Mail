using System.Threading;
using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Ai;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.ContentProcessing;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Common;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Intelligence;

/// <summary>
/// The composer's rewrite of the author's own draft: eligibility, the Composing context, the size
/// limit, and the mode catalog it offers.
/// </summary>
public class DraftRewriteCoordinatorTests
{
    // Mirrors AiEmail:AllowedRewriteModes in the Wino Mail API (appsettings.json and
    // AiEmailOptions). A mode outside this list fails with validation_failed.
    private static readonly string[] ApiAllowedRewriteModes = ["polite", "angry", "formal", "friendly", "shorter", "clearer", "happy"];

    [Fact]
    public void GetRewriteModeOptions_TheComposerCatalog_MatchesTheApiListExactly()
    {
        AiActionCatalog.GetRewriteModeOptions().Select(x => x.Mode).Should().BeEquivalentTo(ApiAllowedRewriteModes);
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
        var html = "<p>" + new string('a', DraftRewriteLimits.MaximumHtmlLength) + "</p>";

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
