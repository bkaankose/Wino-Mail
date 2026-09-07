using System.Collections.Concurrent;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Tests.Helpers;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoPurchaseReconciliationServiceTests : IAsyncLifetime
{
    private readonly InMemoryDatabaseService _database = new();
    private readonly Mock<IWinoAccountProfileService> _profile = new();
    private readonly Mock<IWinoAccountIntelligenceSnapshotService> _snapshots = new();
    private readonly Mock<IWinoPendingCheckoutStore> _pending = new();
    private readonly ManualTimerProvider _time = new();
    private readonly WinoAccount _account = new() { Id = Guid.NewGuid(), Email = "user@example.test" };
    private WinoAccountSessionService _sessions = null!;
    private WinoPurchaseReconciliationService _service = null!;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _sessions = new(_database);
        await _sessions.ReplaceAsync(_account, () => Task.CompletedTask);
        _profile.Setup(x => x.RefreshProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(WinoAccountOperationResult.Success(_account));
        _snapshots.Setup(x => x.RefreshPurchasesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Refresh());
        _service = new(_profile.Object, _snapshots.Object, _sessions, _pending.Object, Mock.Of<IWinoLogger>(), _time);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task Refresh_RequiresFreshProfileEvenWhenCachedBillingGrantsAccess()
    {
        _profile.Setup(x => x.RefreshProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(WinoAccountOperationResult.Failure("offline"));

        (await _service.RefreshAsync()).Outcome.Should().Be(WinoPurchaseRefreshOutcome.Failed);
        _snapshots.Verify(x => x.RefreshPurchasesAsync(It.IsAny<CancellationToken>()), Times.Never);
        _pending.Verify(x => x.Clear(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_RejectsCachedBillingWhenServerRefreshFailed()
    {
        _snapshots.Setup(x => x.RefreshPurchasesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refresh() with { BillingRefreshed = false });

        (await _service.RefreshAsync()).Outcome.Should().Be(WinoPurchaseRefreshOutcome.Failed);
        _pending.Verify(x => x.Clear(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Refresh_OptionalSectionFailureDoesNotInvalidateFreshBilling()
    {
        _snapshots.Setup(x => x.RefreshPurchasesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refresh() with { Error = "usage unavailable" });

        (await _service.RefreshAsync()).Outcome.Should().Be(WinoPurchaseRefreshOutcome.Refreshed);
        _profile.Verify(x => x.RefreshProfileAsync(It.IsAny<CancellationToken>()), Times.Once);
        _snapshots.Verify(x => x.GetCachedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Restore_WithExpectedProductMissing_ReturnsPendingAndRetainsCheckout()
    {
        _pending.Setup(x => x.Get(_account.Id)).Returns(WinoAddOnProductType.AI_PACK);
        _snapshots.Setup(x => x.RefreshPurchasesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Refresh(false));

        (await _service.RefreshAsync()).Outcome.Should().Be(WinoPurchaseRefreshOutcome.Pending);
        _pending.Verify(x => x.Clear(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task CheckoutWithoutMatchingPendingPurchase_RefreshesWithoutPolling()
    {
        _snapshots.Setup(x => x.RefreshPurchasesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Refresh(false));

        (await _service.RefreshAsync(checkoutCompleted: true)).Outcome.Should().Be(WinoPurchaseRefreshOutcome.Refreshed);
        _snapshots.Verify(x => x.RefreshPurchasesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Checkout_RetriesUntilExpectedProductAppears()
    {
        _pending.Setup(x => x.Get(_account.Id)).Returns(WinoAddOnProductType.AI_PACK);
        _snapshots.SetupSequence(x => x.RefreshPurchasesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refresh(false)).ReturnsAsync(Refresh(true));
        var operation = _service.RefreshAsync(checkoutCompleted: true);

        await _time.FireAsync(TimeSpan.FromSeconds(1));

        (await operation.WaitAsync(TimeSpan.FromSeconds(5))).Outcome.Should().Be(WinoPurchaseRefreshOutcome.Refreshed);
        _snapshots.Verify(x => x.RefreshPurchasesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        _pending.Verify(x => x.Clear(_account.Id), Times.Once);
    }

    [Fact]
    public async Task Checkout_StopsAfterThirtySecondsWithPendingOutcome()
    {
        _pending.Setup(x => x.Get(_account.Id)).Returns(WinoAddOnProductType.AI_PACK);
        _snapshots.Setup(x => x.RefreshPurchasesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Refresh(false));
        var operation = _service.RefreshAsync(checkoutCompleted: true);
        await _time.WaitForTimerAsync(TimeSpan.FromSeconds(1));

        await _time.FireAsync(TimeSpan.FromSeconds(30));

        (await operation.WaitAsync(TimeSpan.FromSeconds(5))).Outcome.Should().Be(WinoPurchaseRefreshOutcome.Pending);
        _pending.Verify(x => x.Clear(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentCallbacks_ShareRefreshAndOneCallerCancellationDoesNotCancelOther()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _profile.Setup(x => x.RefreshProfileAsync(It.IsAny<CancellationToken>())).Returns(async (CancellationToken token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return WinoAccountOperationResult.Success(_account);
        });
        using var cancellation = new CancellationTokenSource();
        var first = _service.RefreshAsync(true, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = _service.RefreshAsync(true);
        // CaptureAsync is asynchronous; wait until both callers have reached the shared refresh.
        await Task.Delay(50);
        cancellation.Cancel();
        var canceled = () => first;
        await canceled.Should().ThrowAsync<OperationCanceledException>();
        release.SetResult();

        (await second.WaitAsync(TimeSpan.FromSeconds(5))).Outcome.Should().Be(WinoPurchaseRefreshOutcome.Refreshed);
        _profile.Verify(x => x.RefreshProfileAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CanceledOnlyWaiter_DoesNotReuseCompletedRefreshOnNextRestore()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.Setup(x => x.Clear(_account.Id)).Callback(() => confirmed.TrySetResult());
        _profile.Setup(x => x.RefreshProfileAsync(It.IsAny<CancellationToken>())).Returns(async (CancellationToken token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return WinoAccountOperationResult.Success(_account);
        });
        using var cancellation = new CancellationTokenSource();
        var abandoned = _service.RefreshAsync(cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var canceled = () => abandoned;
        await canceled.Should().ThrowAsync<OperationCanceledException>();

        release.SetResult();
        await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The caller has gone away, so allow the final shared-task continuation to finish.
        await Task.Delay(50);
        _profile.Setup(x => x.RefreshProfileAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(WinoAccountOperationResult.Failure("offline"));

        (await _service.RefreshAsync()).Outcome.Should().Be(WinoPurchaseRefreshOutcome.Failed);
        _profile.Verify(x => x.RefreshProfileAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task SignOutDuringRefresh_PreventsPurchaseConfirmation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _profile.Setup(x => x.RefreshProfileAsync(It.IsAny<CancellationToken>())).Returns(async (CancellationToken token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return WinoAccountOperationResult.Success(_account);
        });
        var operation = _service.RefreshAsync(true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await _sessions.ReplaceAsync(null, () => Task.CompletedTask);

        (await operation.WaitAsync(TimeSpan.FromSeconds(5))).Outcome.Should().Be(WinoPurchaseRefreshOutcome.SignInRequired);
        _pending.Verify(x => x.Clear(It.IsAny<Guid>()), Times.Never);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UnlimitedAccounts_RequiresBothFreshProfileAndBilling(bool profileAccess, bool billingAccess)
    {
        _account.IsUnlimitedAccountsEnabled = profileAccess;
        _pending.Setup(x => x.Get(_account.Id)).Returns(WinoAddOnProductType.UNLIMITED_ACCOUNTS);
        _snapshots.Setup(x => x.RefreshPurchasesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Refresh(unlimited: billingAccess));

        (await _service.RefreshAsync()).Outcome.Should().Be(WinoPurchaseRefreshOutcome.Pending);
    }

    private WinoAccountIntelligenceRefreshResult Refresh(bool aiAccess = true, bool unlimited = false)
        => new(WinoAccountIntelligenceSnapshot.Empty(_account.Id) with
        {
            Billing = new BillingStatusResultDto(unlimited, new AiPackBillingStatusDto("active", aiAccess, null, null, null, false))
        }, true, null) { BillingRefreshed = true };

    private sealed class ManualTimerProvider : TimeProvider
    {
        private readonly ConcurrentDictionary<TimeSpan, TaskCompletionSource<ManualTimer>> _timers = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            _timers.GetOrAdd(dueTime, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(timer);
            return timer;
        }

        public Task WaitForTimerAsync(TimeSpan dueTime) => GetTimerAsync(dueTime);
        public async Task FireAsync(TimeSpan dueTime) => (await GetTimerAsync(dueTime)).Fire();
        private Task<ManualTimer> GetTimerAsync(TimeSpan dueTime)
            => _timers.GetOrAdd(dueTime, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(5));

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            public void Fire() { if (!_disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
