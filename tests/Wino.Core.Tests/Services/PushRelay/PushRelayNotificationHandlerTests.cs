using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Services.PushRelay;
using Xunit;

namespace Wino.Core.Tests.Services.PushRelay;

public sealed class PushRelayNotificationHandlerTests
{
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Mock<IPushRelayRegistrationService> _registration = new();
    private readonly Mock<IAccountService> _accounts = new();
    private readonly Mock<IApplicationRuntime> _runtime = new();
    private readonly Mock<INotificationBuilder> _notifications = new();
    private readonly PushRelayNotificationHandler _handler;

    public PushRelayNotificationHandlerTests()
    {
        _accounts.Setup(x => x.GetAccountAsync(_accountId)).ReturnsAsync(new MailAccount { Id = _accountId });
        _runtime.Setup(x => x.SynchronizeAccountInboxAsync(_accountId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _notifications.Setup(x => x.CreateLiveNotificationsTestNotificationAsync()).Returns(Task.CompletedTask);
        _handler = new PushRelayNotificationHandler(_registration.Object, _accounts.Object, () => _runtime.Object, () => _notifications.Object);
    }

    [Fact]
    public async Task TestPush_ShowsTheTestNotification()
    {
        await _handler.HandleAsync(new Dictionary<string, string> { [PushRelayPayloadKeys.Kind] = PushRelayPushKind.Test });

        _notifications.Verify(x => x.CreateLiveNotificationsTestNotificationAsync(), Times.Once);
    }

    [Fact]
    public async Task TestPush_StillCountsWhenTheNotificationFails()
    {
        _notifications.Setup(x => x.CreateLiveNotificationsTestNotificationAsync()).ThrowsAsync(new InvalidOperationException("denied"));

        var outcome = await _handler.HandleAsync(new Dictionary<string, string> { [PushRelayPayloadKeys.Kind] = PushRelayPushKind.Test });

        outcome.Should().Be(PushRelayNotificationOutcome.NoData);
        _registration.Verify(x => x.ReportTestPush(), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("resync")]
    public async Task MailboxChanged_RunsOneInboxSync(string? reason)
    {
        var payload = Payload(PushRelayPushKind.MailboxChanged);
        if (reason is not null)
            payload[PushRelayPayloadKeys.Reason] = reason;

        var outcome = await _handler.HandleAsync(payload);

        outcome.Should().Be(PushRelayNotificationOutcome.NewData);
        _runtime.Verify(x => x.SynchronizeAccountInboxAsync(_accountId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailedSync_ReportsFailed()
    {
        _runtime.Setup(x => x.SynchronizeAccountInboxAsync(_accountId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        (await _handler.HandleAsync(Payload(PushRelayPushKind.MailboxChanged))).Should().Be(PushRelayNotificationOutcome.Failed);
    }

    [Fact]
    public async Task UnknownAccount_IsIgnored()
    {
        var payload = Payload(PushRelayPushKind.MailboxChanged);
        payload[PushRelayPayloadKeys.ClientAccountId] = Guid.NewGuid().ToString("D");

        (await _handler.HandleAsync(payload)).Should().Be(PushRelayNotificationOutcome.Ignored);
        _runtime.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TestPush_IsReportedWithoutSync()
    {
        var outcome = await _handler.HandleAsync(new Dictionary<string, string> { [PushRelayPayloadKeys.Kind] = PushRelayPushKind.Test });

        outcome.Should().Be(PushRelayNotificationOutcome.NoData);
        _registration.Verify(x => x.ReportTestPush(), Times.Once);
        _runtime.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Reauth_ReportsStatusRequestsUploadAndSyncs()
    {
        var payload = Payload(PushRelayPushKind.MailboxReauth);
        payload[PushRelayPayloadKeys.Status] = PushRelayMailboxStatuses.NeedsReauth;
        payload[PushRelayPayloadKeys.MonitoringReason] = "auth";
        payload[PushRelayPayloadKeys.MonitoringMessage] = "Login failed";

        var outcome = await _handler.HandleAsync(payload);

        outcome.Should().Be(PushRelayNotificationOutcome.NewData);
        _registration.Verify(x => x.ReportMailboxStatus(_accountId, PushRelayMailboxStatuses.NeedsReauth, "auth", "Login failed"), Times.Once);
        _registration.Verify(x => x.RequestSync(PushRelaySyncReason.CredentialsChanged), Times.Once);
        _runtime.Verify(x => x.SynchronizeAccountInboxAsync(_accountId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StatusPush_IsReportedWithoutSync()
    {
        var payload = Payload(PushRelayPushKind.MailboxStatus);
        payload[PushRelayPayloadKeys.Status] = PushRelayMailboxStatuses.Active;

        (await _handler.HandleAsync(payload)).Should().Be(PushRelayNotificationOutcome.NoData);
        _registration.Verify(x => x.ReportMailboxStatus(_accountId, PushRelayMailboxStatuses.Active, null, null), Times.Once);
        _runtime.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("unknown.kind", null)]
    [InlineData(PushRelayPushKind.MailboxChanged, "not-a-guid")]
    [InlineData(PushRelayPushKind.MailboxChanged, "")]
    public async Task MalformedPayloads_AreIgnored(string? kind, string? clientAccountId)
    {
        var payload = new Dictionary<string, string>();
        if (kind is not null)
            payload[PushRelayPayloadKeys.Kind] = kind;
        if (clientAccountId is not null)
            payload[PushRelayPayloadKeys.ClientAccountId] = clientAccountId;

        (await _handler.HandleAsync(payload)).Should().Be(PushRelayNotificationOutcome.Ignored);
        _runtime.VerifyNoOtherCalls();
    }

    private Dictionary<string, string> Payload(string kind) => new()
    {
        [PushRelayPayloadKeys.Version] = "1",
        [PushRelayPayloadKeys.Kind] = kind,
        [PushRelayPayloadKeys.ClientAccountId] = _accountId.ToString("D"),
        [PushRelayPayloadKeys.SubscriptionId] = Guid.NewGuid().ToString("D")
    };
}
