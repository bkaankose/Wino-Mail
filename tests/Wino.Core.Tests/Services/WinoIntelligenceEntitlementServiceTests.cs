using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Messaging.UI;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoIntelligenceEntitlementServiceTests
{
    [Fact]
    public async Task RefreshCompletedAfterSignOut_CannotRestoreAccess()
    {
        var account = new WinoAccount { Id = Guid.NewGuid() };
        var session = new WinoAccountSession(account.Id, 1, CancellationToken.None);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotService = new Mock<IWinoAccountIntelligenceSnapshotService>();
        snapshotService.Setup(service => service.RefreshAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                refreshStarted.TrySetResult();
                await releaseRefresh.Task;
                return new WinoAccountIntelligenceRefreshResult(
                    WinoAccountIntelligenceSnapshot.Empty(account.Id) with
                    {
                        Billing = new BillingStatusResultDto(
                            false,
                            new AiPackBillingStatusDto(
                                "active",
                                true,
                                DateTimeOffset.UtcNow.AddDays(-1),
                                DateTimeOffset.UtcNow.AddDays(30),
                                DateTimeOffset.UtcNow.AddDays(30),
                                false)),
                    },
                    AnySectionUpdated: true,
                    Error: null)
                {
                    BillingRefreshed = true,
                };
            });
        var sessions = new Mock<IWinoAccountSessionService>();
        sessions.Setup(service => service.CaptureAsync(It.IsAny<CancellationToken>())).ReturnsAsync(session);
        sessions.Setup(service => service.IsCurrentAsync(session, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var messenger = new StrongReferenceMessenger();
        using var service = new WinoIntelligenceEntitlementService(snapshotService.Object, sessions.Object, messenger);

        var refresh = service.RefreshAsync();
        await refreshStarted.Task;
        messenger.Send(new WinoAccountSignedOutMessage(account));
        releaseRefresh.TrySetResult();
        await refresh;

        service.Current.State.Should().Be(WinoIntelligenceEntitlementState.SignedOut);
        service.Current.CanAccessSurfaces.Should().BeFalse();
    }
}
