using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.ContentProcessing;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoIntelligenceCoordinatorTests
{
    [Theory]
    [InlineData(false, true, true, true, true, MailProviderType.Outlook, false, false, false)]
    [InlineData(true, false, true, true, true, MailProviderType.Outlook, false, false, false)]
    [InlineData(true, true, false, false, true, MailProviderType.Outlook, false, false, false)]
    [InlineData(true, true, true, false, true, MailProviderType.Outlook, true, true, false)]
    [InlineData(true, true, false, true, true, MailProviderType.Outlook, true, false, true)]
    [InlineData(true, true, true, true, false, MailProviderType.Outlook, true, true, false)]
    [InlineData(true, true, true, true, true, (MailProviderType)99, true, true, false)]
    [InlineData(true, true, false, true, true, (MailProviderType)99, false, false, false)]
    [InlineData(true, true, true, true, true, MailProviderType.IMAP4, true, true, true)]
    public async Task Snapshot_GatesActionsByAccountPurchaseConsentPreferenceAndProvider(
        bool authenticated,
        bool hasAiPack,
        bool transportConsent,
        bool processConsent,
        bool indexingEnabled,
        MailProviderType providerType,
        bool expectedVisible,
        bool expectedSummary,
        bool expectedProcessing)
    {
        var localAccountId = Guid.NewGuid();
        var mailboxId = Guid.NewGuid();
        var profile = new Mock<IWinoAccountProfileService>();
        profile.Setup(x => x.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(authenticated ? new WinoAccount { Id = Guid.NewGuid() } : null);
        var billing = new Mock<IWinoBillingService>();
        billing.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            ApiEnvelope<BillingStatusResultDto>.Success(new BillingStatusResultDto(
                false,
                new AiPackBillingStatusDto("active", hasAiPack, null, null, null, false))));
        var api = new Mock<IWinoAccountApiClient>();
        api.Setup(x => x.GetTransportConsentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            TransportConsent(transportConsent));
        api.Setup(x => x.GetProcessConsentsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new ProcessConsentListDto("process-v2", "https://example.test/privacy",
            [ProcessConsent(mailboxId, providerType, processConsent)]));
        var resolver = new Mock<IIntelligenceMessageContextResolver>();
        resolver.Setup(x => x.FindCandidateAsync(localAccountId, "provider-message", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Candidate());
        var semantic = new Mock<ISemanticIndexCoordinator>();
        semantic.Setup(x => x.GetMessageStateAsync(localAccountId, "provider-message", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SemanticMessageIndexState.NotIndexed);
        var mime = new Mock<IMimeFileService>();

        using var coordinator = new WinoIntelligenceCoordinator(
            profile.Object,
            billing.Object,
            api.Object,
            semantic.Object,
            resolver.Object,
            Mock.Of<ILocalIntelligenceStore>(),
            mime.Object,
            Mock.Of<IMailService>(),
            Mock.Of<IAccountService>(),
            Mock.Of<IWinoRequestDelegator>(),
            Mock.Of<ITranslationService>(),
            Mock.Of<IWinoLogger>(),
            Mock.Of<IIntelligenceBackend>(),
            Mock.Of<IContentEnvelopeEncryptor>(),
            new MailContentProjector());

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
    }

    [Fact]
    public async Task Snapshot_RejectsOutdatedConsentPolicyVersions()
    {
        var localAccountId = Guid.NewGuid();
        var profile = new Mock<IWinoAccountProfileService>();
        profile.Setup(x => x.GetAuthenticatedAccountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WinoAccount { Id = Guid.NewGuid() });
        var billing = new Mock<IWinoBillingService>();
        billing.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            ApiEnvelope<BillingStatusResultDto>.Success(new BillingStatusResultDto(
                false, new AiPackBillingStatusDto("active", true, null, null, null, false))));
        var api = new Mock<IWinoAccountApiClient>();
        api.Setup(x => x.GetTransportConsentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new TransportConsentDto(ConsentStatuses.Active, "transport-v2", "transport-v1", DateTimeOffset.UtcNow, null, "https://example.test/privacy"));
        api.Setup(x => x.GetProcessConsentsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
            new ProcessConsentListDto("process-v2", "https://example.test/privacy",
            [new MailboxProcessConsentDto(Guid.NewGuid(), "mail@example.test", (int)MailProviderType.Outlook,
                ConsentStatuses.Active, "process-v2", "process-v1", DateTimeOffset.UtcNow, null, null, "https://example.test/privacy")]));

        using var coordinator = CreateCoordinator(profile, billing, api);
        var snapshot = await coordinator.GetSnapshotAsync(Context(localAccountId));

        snapshot.Should().Be(WinoIntelligenceSnapshot.Hidden);
    }

    private static WinoIntelligenceCoordinator CreateCoordinator(
        Mock<IWinoAccountProfileService> profile,
        Mock<IWinoBillingService> billing,
        Mock<IWinoAccountApiClient> api)
        => new(
            profile.Object, billing.Object, api.Object, Mock.Of<ISemanticIndexCoordinator>(),
            Mock.Of<IIntelligenceMessageContextResolver>(), Mock.Of<ILocalIntelligenceStore>(),
            Mock.Of<IMimeFileService>(), Mock.Of<IMailService>(), Mock.Of<IAccountService>(),
            Mock.Of<IWinoRequestDelegator>(), Mock.Of<ITranslationService>(), Mock.Of<IWinoLogger>(),
            Mock.Of<IIntelligenceBackend>(), Mock.Of<IContentEnvelopeEncryptor>(), new MailContentProjector());

    private static WinoIntelligenceContext Context(Guid localAccountId)
        => new("content", localAccountId, Guid.NewGuid(), Guid.NewGuid(), "provider-message",
            "mail@example.test", MailProviderType.Outlook, true, "Subject", "sender@example.test",
            DateTimeOffset.UtcNow, "<p>Body</p>");

    private static TransportConsentDto TransportConsent(bool current)
        => new(current ? ConsentStatuses.Active : ConsentStatuses.NotAccepted, "transport-v2",
            current ? "transport-v2" : null, current ? DateTimeOffset.UtcNow : null, null,
            "https://example.test/privacy");

    private static MailboxProcessConsentDto ProcessConsent(Guid mailboxId, MailProviderType providerType, bool current)
        => new(mailboxId, "mail@example.test", (int)providerType,
            current ? ConsentStatuses.Active : ConsentStatuses.NotAccepted, "process-v2",
            current ? "process-v2" : null, current ? DateTimeOffset.UtcNow : null, null, null,
            "https://example.test/privacy");

    private static IntelligenceMessageCandidate Candidate()
        => new(Guid.NewGuid(), "remote-message", "provider-message", [Guid.NewGuid()], "Subject",
            "sender@example.test", DateTime.UtcNow, null, false, false, "normal", ["inbox"],
            new MailBodyLocator("remote-message", "inbox", ProviderMessageId: "provider-message"));
}
