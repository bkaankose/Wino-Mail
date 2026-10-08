using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;
using Wino.Messaging.UI;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoAccountIntelligenceSnapshotServiceEntitlementTests
{
    [Fact]
    public async Task RefreshCompletedAfterSignOut_CannotRestoreAccess()
    {
        var account = new WinoAccount { Id = Guid.NewGuid() };
        var session = new WinoAccountSession(account.Id, 1, CancellationToken.None);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var billing = new Mock<IWinoBillingService>();
        billing.Setup(service => service.GetStatusAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                refreshStarted.TrySetResult();
                await releaseRefresh.Task;
                return ApiEnvelope<BillingStatusResultDto>.Success(new BillingStatusResultDto(
                    false,
                    new AiPackBillingStatusDto(
                        "active",
                        true,
                        DateTimeOffset.UtcNow.AddDays(-1),
                        DateTimeOffset.UtcNow.AddDays(30),
                        DateTimeOffset.UtcNow.AddDays(30),
                        false)));
            });
        var sessions = new Mock<IWinoAccountSessionService>();
        sessions.Setup(service => service.CaptureAsync(It.IsAny<CancellationToken>())).ReturnsAsync(session);
        sessions.Setup(service => service.IsCurrentAsync(session, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var messenger = new StrongReferenceMessenger();
        using var service = new WinoAccountIntelligenceSnapshotService(
            billing.Object,
            Mock.Of<IWinoAccountApiClient>(),
            Mock.Of<IMailIntelligenceStore>(),
            sessions.Object,
            messenger);

        var refresh = service.RefreshEntitlementAsync();
        await refreshStarted.Task;
        messenger.Send(new WinoAccountSignedOutMessage(account));
        releaseRefresh.TrySetResult();
        await refresh;

        service.CurrentEntitlement.State.Should().Be(WinoIntelligenceEntitlementState.SignedOut);
        service.CurrentEntitlement.CanAccessSurfaces.Should().BeFalse();
    }
}
