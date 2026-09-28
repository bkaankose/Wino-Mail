using FluentAssertions;
using Wino.Core.Domain.Exceptions;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public class ImapClientPoolConcurrencyTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ReadValidationTimeoutRetriesOnFreshConnection()
    {
        await using var server = new TestImapServer { HoldFirstNoop = true };
        await using var pool = server.CreatePool();
        var client = await pool.RentForReadAsync(pool.ValidateAsync, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        server.ConnectionCount.Should().Be(2);
        pool.Release(client);
    }

    [Fact]
    public async Task FreshConnectionIsNotValidatedWithRedundantNoop()
    {
        await using var server = new TestImapServer();
        await using var pool = server.CreatePool();
        var client = await pool.RentForReadAsync(CancellationToken.None).WaitAsync(Deadline);
        server.ConnectionCount.Should().Be(1);
        server.Commands.Should().NotContain("NOOP");
        pool.Release(client);
    }

    [Fact]
    public async Task ReadWithoutCreationReturnsNullUntilASpareConnectionExists()
    {
        await using var server = new TestImapServer();
        await using var pool = server.CreatePool(max: 3);
        (await pool.RentForReadAsync(CancellationToken.None, allowCreate: false)).Should().BeNull();
        server.ConnectionCount.Should().Be(0);

        var primary = await pool.RentForReadAsync(CancellationToken.None).WaitAsync(Deadline);
        (await pool.RentForReadAsync(CancellationToken.None, allowCreate: false)).Should().BeNull("the only connection is leased");
        server.ConnectionCount.Should().Be(1);

        pool.Release(primary);
        var spare = await pool.RentForReadAsync(CancellationToken.None, allowCreate: false);
        spare.Should().BeSameAs(primary);
        server.ConnectionCount.Should().Be(1);
        pool.Release(spare);
    }

    [Fact]
    public async Task CallerCancellationDoesNotRetryRead()
    {
        await using var server = new TestImapServer { HoldFirstNoop = true };
        await using var pool = server.CreatePool();
        using var cancel = new CancellationTokenSource();
        var read = pool.RentForReadAsync(pool.ValidateAsync, cancel.Token);
        await server.NoopReceived.Task.WaitAsync(Deadline);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        server.ConnectionCount.Should().Be(1);
        pool.Health.TotalConnections.Should().Be(0);
    }

    [Fact]
    public async Task MaintenanceSkipsBorrowersActiveCommand()
    {
        await using var server = new TestImapServer { HoldFirstNoop = true };
        await using var pool = server.CreatePool();
        var client = await pool.RentAsync();
        var command = client.NoOpAsync();
        await server.NoopReceived.Task.WaitAsync(Deadline);
        await pool.SendNoOpToAvailableClientsAsync(CancellationToken.None).WaitAsync(Deadline);
        server.Commands.Count(x => x == "NOOP").Should().Be(1);
        server.ReleaseNoop.TrySetResult();
        await command.WaitAsync(Deadline);
        pool.Return(client);
    }

    [Fact]
    public async Task SocketClosedDuringInactivityIsValidatedAndReplacedBeforeRent()
    {
        await using var server = new TestImapServer { IdleTimeout = TimeSpan.FromMilliseconds(300) };
        await using var pool = server.CreatePool();
        var stale = await pool.RentAsync();
        pool.Return(stale);
        await server.IdleClosed.Task.WaitAsync(Deadline);
        stale.IsConnected.Should().BeTrue("MailKit has not read the server closure yet");
        stale.LastUsedUtc = DateTime.UtcNow.AddMinutes(-5);
        var fresh = await pool.RentAsync();
        fresh.Should().NotBeSameAs(stale);
        await fresh.NoOpAsync();
        pool.Return(fresh);
        server.ConnectionCount.Should().Be(2);
    }

    [Fact]
    public async Task InitialHandshakeIsCoveredByRentDeadline()
    {
        await using var server = new TestImapServer { HoldGreeting = true };
        await using var pool = server.CreatePool();
        await Assert.ThrowsAsync<ImapClientPoolException>(() => pool.RentAsync(TimeSpan.FromMilliseconds(200)));
        pool.Health.TotalConnections.Should().Be(0);
        server.ReleaseGreeting.TrySetResult();
        var client = await pool.RentAsync().WaitAsync(Deadline);
        pool.Return(client);
    }

    [Fact]
    public async Task ServerConnectionLimitIncludesIdleAndForegroundOwners()
    {
        await using var server = new TestImapServer { SupportsIdle = true, MaxConnections = 2 };
        await using var pool = server.CreatePool(max: 2);
        await pool.PreWarmPoolAsync();
        var idle = await pool.GetIdleClientAsync();
        idle.Should().NotBeNull();
        var command = await pool.RentAsync();
        await Assert.ThrowsAsync<ImapClientPoolException>(() => pool.RentAsync(TimeSpan.FromMilliseconds(200)));
        server.ConnectionCount.Should().Be(2);
        server.RejectedConnections.Should().Be(0);
        pool.Return(command);
        pool.ReleaseIdleClient();
    }

    [Fact]
    public async Task MaintenanceOwnsClientUntilNoopCompletes()
    {
        await using var server = new TestImapServer { HoldFirstNoop = true };
        await using var pool = server.CreatePool();
        var first = await pool.RentAsync();
        pool.Return(first);
        var maintenance = pool.SendNoOpToAvailableClientsAsync(CancellationToken.None);
        await server.NoopReceived.Task.WaitAsync(Deadline);
        var rent = pool.RentAsync(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<ImapClientPoolException>(() => rent);
        server.ConnectionCount.Should().Be(1);
        server.ReleaseNoop.TrySetResult();
        await maintenance.WaitAsync(Deadline);
        var next = await pool.RentAsync();
        next.Should().BeSameAs(first);
        await next.NoOpAsync();
        pool.Return(next);
    }

    [Fact]
    public async Task ConcurrentRentIncludesConnectingClientsInCapacity()
    {
        await using var server = new TestImapServer { HoldGreeting = true };
        await using var pool = server.CreatePool();
        var firstRent = pool.RentAsync();
        await server.Accepted.Task.WaitAsync(Deadline);
        var contenders = Enumerable.Range(0, 4).Select(_ => pool.RentAsync(TimeSpan.FromMilliseconds(200))).ToArray();
        foreach (var contender in contenders) await Assert.ThrowsAsync<ImapClientPoolException>(() => contender);
        server.ConnectionCount.Should().Be(1);
        pool.Health.ReconnectingConnections.Should().Be(1);
        server.ReleaseGreeting.TrySetResult();
        var first = await firstRent.WaitAsync(Deadline);
        pool.Return(first);
    }

    [Fact]
    public async Task ShutdownDrainsOwnerWithoutSendingLogoutAndRejectsNewRent()
    {
        await using var server = new TestImapServer { HoldFirstNoop = true };
        var pool = server.CreatePool();
        var client = await pool.RentAsync();
        var command = client.NoOpAsync();
        await server.NoopReceived.Task.WaitAsync(Deadline);
        var dispose = pool.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pool.RentAsync());
        dispose.IsCompleted.Should().BeFalse();
        server.ReleaseNoop.TrySetResult();
        await command.WaitAsync(Deadline);
        pool.Return(client);
        await dispose.WaitAsync(Deadline);
        pool.Health.TotalConnections.Should().Be(0);
        server.Commands.Should().NotContain("LOGOUT");
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task FirstReadResetRetriesOnceOnNewConnection()
    {
        await using var server = new TestImapServer { ResetFirstNoop = true };
        await using var pool = server.CreatePool();
        var client = await pool.RentForReadAsync((candidate, token) => candidate.NoOpAsync(token), CancellationToken.None);
        server.ConnectionCount.Should().Be(2);
        server.Commands.Count(x => x == "NOOP").Should().Be(2);
        pool.Release(client);
    }

    [Fact]
    public async Task SecondReadFailureIsNotRetried()
    {
        await using var server = new TestImapServer();
        await using var pool = server.CreatePool();
        int attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => pool.RentForReadAsync((_, _) =>
        {
            attempts++;
            throw new IOException("simulated read failure");
        }, CancellationToken.None));
        attempts.Should().Be(2);
        server.ConnectionCount.Should().Be(2);
        pool.Health.TotalConnections.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestPoolOrMissingIdleCapabilityDoesNotOpenListener(bool testPool)
    {
        await using var server = new TestImapServer { SupportsIdle = testPool };
        await using var pool = server.CreatePool(max: 5, test: testPool);
        (await pool.GetIdleClientAsync()).Should().BeNull();
        server.ConnectionCount.Should().Be(1);
        pool.Health.IdleConnectionActive.Should().BeFalse();
        if (testPool)
        {
            await pool.PreWarmPoolAsync();
            server.ConnectionCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task StalledKeepaliveHasBoundedTimeoutAndRetiresSocket()
    {
        await using var server = new TestImapServer { HoldFirstNoop = true };
        await using var pool = server.CreatePool();
        await pool.InitializeAsync();
        await pool.SendNoOpToAvailableClientsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        pool.Health.TotalConnections.Should().Be(0);
    }
}
