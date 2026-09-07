using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Tests.Helpers;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoAccountIntelligenceSnapshotServiceTests : IAsyncLifetime
{
    private readonly InMemoryDatabaseService _database = new();
    private readonly Mock<IWinoBillingService> _billing = new();
    private readonly Mock<IWinoAccountApiClient> _api = new();
    private readonly Mock<ILocalIntelligenceStore> _store = new();
    private WinoAccountSessionService _sessions = null!;
    private WinoAccountIntelligenceSnapshotService _service = null!;
    private readonly Guid _accountId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _sessions = new(_database);
        await _sessions.ReplaceAsync(new WinoAccount { Id = _accountId }, () => Task.CompletedTask);
        _api.Setup(x => x.GetIntelligenceConsentAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Offline"));
        _store.Setup(x => x.SaveAccountIntelligenceSnapshotAsync(It.IsAny<WinoAccountIntelligenceSnapshot>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _service = new(_billing.Object, _api.Object, _store.Object, _sessions);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task SaveFromPreviousSession_CannotRestoreDataAfterSameAccountSignsInAgain()
    {
        var previous = await _sessions.CaptureAsync();
        var snapshot = WinoAccountIntelligenceSnapshot.Empty(_accountId) with { Session = previous };
        await _sessions.ReplaceAsync(null, () => Task.CompletedTask);
        await _sessions.ReplaceAsync(new WinoAccount { Id = _accountId }, () => Task.CompletedTask);

        await _service.SaveAsync(snapshot);

        _store.Verify(x => x.SaveAccountIntelligenceSnapshotAsync(It.IsAny<WinoAccountIntelligenceSnapshot>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentBackgroundRefreshes_ShareOneNetworkRequest()
    {
        var started = Signal();
        var release = Signal();
        _billing.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            started.TrySetResult();
            await release.Task;
            return Billing(false);
        });

        var first = _service.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = _service.RefreshAsync();
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        results[0].Should().BeSameAs(results[1]);
        _billing.Verify(x => x.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(x => x.SaveAccountIntelligenceSnapshotAsync(It.IsAny<WinoAccountIntelligenceSnapshot>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PurchaseRefreshDuringBackground_QueuesANewAuthoritativeRead()
    {
        var started = Signal();
        var release = Signal();
        var requests = 0;
        _billing.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            var request = Interlocked.Increment(ref requests);
            if (request == 1)
            {
                started.SetResult();
                await release.Task;
            }
            return Billing(request > 1);
        });

        var background = _service.RefreshAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var purchase = _service.RefreshPurchasesAsync();
        purchase.IsCompleted.Should().BeFalse();
        release.SetResult();
        await background;
        var result = await purchase;

        requests.Should().Be(2);
        result!.BillingRefreshed.Should().BeTrue();
        result.Snapshot.Billing!.IsUnlimitedAccountsEnabled.Should().BeTrue();
        _api.Verify(x => x.GetIntelligenceConsentAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveWhileSignedOut_DoesNotWriteCache()
    {
        await _sessions.ReplaceAsync(null, () => Task.CompletedTask);

        await _service.SaveAsync(WinoAccountIntelligenceSnapshot.Empty(_accountId));

        _store.Verify(x => x.SaveAccountIntelligenceSnapshotAsync(It.IsAny<WinoAccountIntelligenceSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NetworkCompletionAfterSignOut_DoesNotRepopulateCache()
    {
        var started = Signal();
        var release = Signal();
        _billing.Setup(x => x.GetStatusAsync(It.IsAny<CancellationToken>())).Returns(async () =>
        {
            started.SetResult();
            await release.Task;
            return Billing(true);
        });

        var pending = _service.RefreshPurchasesAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _sessions.ReplaceAsync(null, () => Task.CompletedTask);
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        _store.Verify(x => x.SaveAccountIntelligenceSnapshotAsync(It.IsAny<WinoAccountIntelligenceSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ApiEnvelope<BillingStatusResultDto> Billing(bool unlimited)
        => ApiEnvelope<BillingStatusResultDto>.Success(new BillingStatusResultDto(unlimited, null!));
}
