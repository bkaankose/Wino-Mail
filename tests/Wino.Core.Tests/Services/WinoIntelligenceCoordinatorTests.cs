using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.ContentProcessing;
using Wino.Mail.Contracts.Intelligence;
using Wino.Mail.Contracts.SemanticIndex;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoIntelligenceCoordinatorTests
{
    [Theory]
    [InlineData(false, true, true, true, MailProviderType.Outlook, false, false, false)]
    [InlineData(true, false, true, true, MailProviderType.Outlook, false, false, false)]
    [InlineData(true, true, false, true, MailProviderType.Outlook, true, false, false)]
    [InlineData(true, true, true, false, MailProviderType.Outlook, true, true, false)]
    [InlineData(true, true, true, true, (MailProviderType)99, true, true, false)]
    [InlineData(true, true, true, true, MailProviderType.IMAP4, true, true, true)]
    public async Task Snapshot_GatesActionsByAccountPurchaseConsentPreferenceAndProvider(
        bool authenticated,
        bool hasAiPack,
        bool intelligenceConsent,
        bool indexingEnabled,
        MailProviderType providerType,
        bool expectedVisible,
        bool expectedSummary,
        bool expectedProcessing)
    {
        var localAccountId = Guid.NewGuid();
        var winoAccountId = Guid.NewGuid();
        var mailboxId = Guid.NewGuid();
        var profile = new Mock<IWinoAccountProfileService>();
        profile.Setup(x => x.GetActiveAccountAsync())
            .ReturnsAsync(authenticated ? new WinoAccount { Id = winoAccountId } : null);
        var api = new Mock<IWinoAccountApiClient>(MockBehavior.Strict);
        var accountSnapshotService = new Mock<IWinoAccountIntelligenceSnapshotService>();
        accountSnapshotService
            .Setup(x => x.GetCachedAsync(winoAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AccountSnapshot(winoAccountId, hasAiPack, intelligenceConsent, mailboxId, providerType));
        var resolver = new Mock<IIntelligenceMessageContextResolver>();
        resolver.Setup(x => x.FindCandidateAsync(localAccountId, "provider-message", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Candidate());
        var semantic = new Mock<ISemanticIndexCoordinator>();
        semantic.Setup(x => x.GetMessageStateAsync(localAccountId, "provider-message", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SemanticMessageIndexState.NotIndexed);
        var mime = new Mock<IMimeFileService>();

        using var coordinator = new WinoIntelligenceCoordinator(
            profile.Object,
            api.Object,
            semantic.Object,
            resolver.Object,
            Mock.Of<ILocalIntelligenceStore>(),
            mime.Object,
            Mock.Of<IMailService>(),
            Mock.Of<IAccountService>(),
            Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<ITranslationService>(),
            Mock.Of<IPreferencesService>(),
            Mock.Of<IWinoLogger>(),
            Mock.Of<IIntelligenceBackend>(),
            Mock.Of<IContentEnvelopeEncryptor>(),
            new MailContentProjector(),
            accountSnapshotService.Object);

        var snapshot = await coordinator.GetSnapshotAsync(new WinoIntelligenceContext(
            "content", localAccountId, Guid.NewGuid(), Guid.NewGuid(), "provider-message",
            "mail@example.test", providerType, indexingEnabled, "Subject", "sender@example.test",
            DateTimeOffset.UtcNow, "<p>Body</p>"));

        snapshot.IsVisible.Should().Be(expectedVisible);
        snapshot.IsSummaryAvailable.Should().Be(expectedSummary);
        snapshot.IsTranslateAvailable.Should().Be(expectedSummary);
        snapshot.IsProcessingAvailable.Should().Be(expectedProcessing);
        snapshot.IsSuggestedRepliesAvailable.Should().Be(expectedProcessing);
        snapshot.IsFindSimilarAvailable.Should().Be(expectedProcessing);
        api.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Snapshot_RejectsOutdatedConsentActionsButKeepsAddonHeaderVisible()
    {
        var localAccountId = Guid.NewGuid();
        var winoAccountId = Guid.NewGuid();
        var profile = new Mock<IWinoAccountProfileService>();
        profile.Setup(x => x.GetActiveAccountAsync()).ReturnsAsync(new WinoAccount { Id = winoAccountId });
        var api = new Mock<IWinoAccountApiClient>(MockBehavior.Strict);
        var accountSnapshotService = new Mock<IWinoAccountIntelligenceSnapshotService>();
        accountSnapshotService
            .Setup(x => x.GetCachedAsync(winoAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WinoAccountIntelligenceSnapshot(
                winoAccountId,
                BillingStatus(hasAccess: true),
                new IntelligenceConsentDto(ConsentStatuses.Active, "intelligence-v2", "intelligence-v1",
                    DateTimeOffset.UtcNow, null, "https://example.test/privacy", IntelligenceDeletionStatuses.NotRequired),
                null,
                [],
                new Dictionary<Guid, IntelligenceMailboxStatusDto>(),
                null, null, null, null, null, null));

        using var coordinator = CreateCoordinator(profile, api, accountSnapshotService.Object);
        var snapshot = await coordinator.GetSnapshotAsync(Context(localAccountId));

        snapshot.IsVisible.Should().BeTrue();
        snapshot.IsSummaryAvailable.Should().BeFalse();
        snapshot.IsTranslateAvailable.Should().BeFalse();
        snapshot.IsProcessingAvailable.Should().BeFalse();
        api.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Snapshot_WithNoLocalAccessSnapshot_DoesNotCallAccountApi()
    {
        var localAccountId = Guid.NewGuid();
        var winoAccountId = Guid.NewGuid();
        var profile = new Mock<IWinoAccountProfileService>();
        profile.Setup(x => x.GetActiveAccountAsync()).ReturnsAsync(new WinoAccount { Id = winoAccountId });
        var api = new Mock<IWinoAccountApiClient>(MockBehavior.Strict);
        var accountSnapshotService = new Mock<IWinoAccountIntelligenceSnapshotService>();
        accountSnapshotService
            .Setup(x => x.GetCachedAsync(winoAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WinoAccountIntelligenceSnapshot?)null);
        var localStore = new Mock<ILocalIntelligenceStore>();
        localStore
            .Setup(x => x.GetAccessSnapshotAsync(localAccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LocalIntelligenceAccessSnapshot?)null);

        using var coordinator = new WinoIntelligenceCoordinator(
            profile.Object, api.Object, Mock.Of<ISemanticIndexCoordinator>(),
            Mock.Of<IIntelligenceMessageContextResolver>(), localStore.Object,
            Mock.Of<IMimeFileService>(), Mock.Of<IMailService>(), Mock.Of<IAccountService>(),
            Mock.Of<IWinoRequestDelegator>(), Mock.Of<ITranslationService>(), Mock.Of<IPreferencesService>(),
            Mock.Of<IWinoLogger>(),
            Mock.Of<IIntelligenceBackend>(), Mock.Of<IContentEnvelopeEncryptor>(), new MailContentProjector(),
            accountSnapshotService.Object);

        var snapshot = await coordinator.GetSnapshotAsync(Context(localAccountId));

        snapshot.Should().Be(WinoIntelligenceSnapshot.Hidden);
        profile.Verify(x => x.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>()), Times.Never);
        api.VerifyNoOtherCalls();
    }

    private static WinoIntelligenceCoordinator CreateCoordinator(
        Mock<IWinoAccountProfileService> profile,
        Mock<IWinoAccountApiClient> api,
        IWinoAccountIntelligenceSnapshotService accountSnapshotService)
        => new(
            profile.Object, api.Object, Mock.Of<ISemanticIndexCoordinator>(),
            Mock.Of<IIntelligenceMessageContextResolver>(), Mock.Of<ILocalIntelligenceStore>(),
            Mock.Of<IMimeFileService>(), Mock.Of<IMailService>(), Mock.Of<IAccountService>(),
            Mock.Of<IWinoRequestDelegator>(), Mock.Of<ITranslationService>(), Mock.Of<IPreferencesService>(),
            Mock.Of<IWinoLogger>(),
            Mock.Of<IIntelligenceBackend>(), Mock.Of<IContentEnvelopeEncryptor>(), new MailContentProjector(),
            accountSnapshotService);

    private static WinoAccountIntelligenceSnapshot AccountSnapshot(
        Guid winoAccountId,
        bool hasAiPack,
        bool intelligenceConsent,
        Guid mailboxId,
        MailProviderType providerType)
        => new(
            winoAccountId,
            BillingStatus(hasAiPack),
            IntelligenceConsent(intelligenceConsent),
            null,
            [new SemanticMailboxDto(mailboxId, "mail@example.test", (int)providerType, null)],
            new Dictionary<Guid, IntelligenceMailboxStatusDto>(),
            null, null, null, null, null, null);

    private static BillingStatusResultDto BillingStatus(bool hasAccess)
        => new(false, new AiPackBillingStatusDto("active", hasAccess, null, null, null, false));

    private static WinoIntelligenceContext Context(Guid localAccountId)
        => new("content", localAccountId, Guid.NewGuid(), Guid.NewGuid(), "provider-message",
            "mail@example.test", MailProviderType.Outlook, true, "Subject", "sender@example.test",
            DateTimeOffset.UtcNow, "<p>Body</p>");

    private static IntelligenceConsentDto IntelligenceConsent(bool current)
        => new(current ? ConsentStatuses.Active : ConsentStatuses.NotAccepted, "intelligence-v2",
            current ? "intelligence-v2" : null, current ? DateTimeOffset.UtcNow : null, null,
            "https://example.test/privacy", IntelligenceDeletionStatuses.NotRequired);

    private static IntelligenceMessageCandidate Candidate()
        => new(Guid.NewGuid(), "remote-message", "provider-message", [Guid.NewGuid()], "Subject",
            "sender@example.test", DateTime.UtcNow, null, false, false, "normal", ["inbox"],
            new MailBodyLocator("remote-message", "inbox", ProviderMessageId: "provider-message"));
}
