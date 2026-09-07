using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoAccountSessionServiceTests : IAsyncLifetime
{
    private readonly InMemoryDatabaseService _database = new();
    private WinoAccountSessionService _sessions = null!;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _sessions = new(_database);
        await _sessions.ReplaceAsync(Account(), () => Task.CompletedTask);
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task SignOut_InvalidatesCapturedSessionAndRejectsLateCommit()
    {
        var session = (await _sessions.CaptureAsync())!;
        await _sessions.ReplaceAsync(null, () => Task.CompletedTask);
        var called = false;

        var committed = await _sessions.CommitAsync(session, () => { called = true; return Task.CompletedTask; });

        committed.Should().BeFalse();
        called.Should().BeFalse();
        session.CancellationToken.IsCancellationRequested.Should().BeTrue();
        (await _sessions.CaptureAsync()).Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentRejectedTokenRefreshes_RotateOnlyOnceEvenWhenTokenNotExpired()
    {
        var session = (await _sessions.CaptureAsync())!;
        var requests = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<WinoAccount?> Refresh(WinoAccount account, CancellationToken token)
        {
            Interlocked.Increment(ref requests);
            started.SetResult();
            await release.Task;
            account.AccessToken = "rotated";
            return account;
        }

        var first = _sessions.RefreshCredentialsAsync(session, "original", Refresh);
        await started.Task;
        var second = _sessions.RefreshCredentialsAsync(session, "original", Refresh);
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        requests.Should().Be(1);
        results.Should().OnlyContain(account => account!.AccessToken == "rotated");
    }

    [Fact]
    public async Task AccountSwitch_RejectsOldRefreshEvenWhenNetworkIgnoresCancellation()
    {
        var session = (await _sessions.CaptureAsync())!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = _sessions.RefreshCredentialsAsync(session, "original", async (account, token) =>
        {
            started.SetResult();
            await release.Task;
            account.AccessToken = "late";
            return account;
        });
        await started.Task;
        var replacement = Account();
        await _sessions.ReplaceAsync(replacement, () => Task.CompletedTask);
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        (await _database.Connection.Table<WinoAccount>().ToListAsync()).Should().ContainSingle()
            .Which.Id.Should().Be(replacement.Id);
    }

    [Fact]
    public async Task CredentialRefresh_PreservesProfileCommittedWhileRequestWasRunning()
    {
        var session = (await _sessions.CaptureAsync())!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = _sessions.RefreshCredentialsAsync(session, "original", async (account, token) =>
        {
            started.SetResult();
            await release.Task;
            account.AccessToken = "rotated";
            return account;
        });
        await started.Task;
        await _sessions.CommitAsync(session, async () =>
        {
            var account = await _database.Connection.Table<WinoAccount>().FirstAsync();
            account.IsUnlimitedAccountsEnabled = true;
            await _database.Connection.UpdateAsync(account);
        });
        release.SetResult();

        var result = await pending;

        result!.AccessToken.Should().Be("rotated");
        result.IsUnlimitedAccountsEnabled.Should().BeTrue();
        (await _database.Connection.Table<WinoAccount>().FirstAsync()).IsUnlimitedAccountsEnabled.Should().BeTrue();
    }

    private static WinoAccount Account() => new()
    {
        Id = Guid.NewGuid(), AccessToken = "original", RefreshToken = "refresh",
        AccessTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1)
    };
}
