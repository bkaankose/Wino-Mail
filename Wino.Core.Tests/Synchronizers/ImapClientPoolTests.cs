using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Models.Connectivity;
using Wino.Core.Integration;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public class ImapClientPoolTests
{
    [Fact]
    public void CalculateMaxConnections_ShouldUseDefault_WhenConfiguredValueIsNonPositive()
    {
        ImapClientPool.CalculateMaxConnections(0).Should().Be(5);
        ImapClientPool.CalculateMaxConnections(-4).Should().Be(5);
    }

    [Fact]
    public void CalculateMaxConnections_ShouldClampToTen_WhenConfiguredValueIsTooHigh()
    {
        ImapClientPool.CalculateMaxConnections(40).Should().Be(10);
    }

    [Fact]
    public void CalculateTargetMinimumConnections_ShouldRespectConservativeMode()
    {
        ImapClientPool.CalculateTargetMinimumConnections(maxConnections: 5, useConservativeConnections: true).Should().Be(1);
    }

    [Fact]
    public void CalculateTargetMinimumConnections_ShouldBeTwo_WhenNotConservativeAndCapacityAllows()
    {
        ImapClientPool.CalculateTargetMinimumConnections(maxConnections: 5, useConservativeConnections: false).Should().Be(2);
    }

    [Fact]
    public async Task RentAsync_ShouldThrowImapClientPoolException_WhenAcquireTimesOut()
    {
        var serverInformation = new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            IncomingServer = "127.0.0.1",
            IncomingServerPort = "1",
            IncomingServerUsername = "user",
            IncomingServerPassword = "password",
            IncomingServerSocketOption = ImapConnectionSecurity.None,
            IncomingAuthenticationMethod = ImapAuthenticationMethod.Auto,
            MaxConcurrentClients = 2
        };

        using var pool = new ImapClientPool(ImapClientPoolOptions.CreateTestPool(serverInformation, protocolLog: null));

        var act = async () => await pool.RentAsync(TimeSpan.FromMilliseconds(400));
        var exception = await act.Should().ThrowAsync<ImapClientPoolException>();

        exception.Which.CustomServerInformation.Should().NotBeNull();
    }

    [Fact]
    public async Task InitializeAsync_ShouldBeSafe_WhenCalledConcurrently()
    {
        var serverInformation = new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            IncomingServer = "127.0.0.1",
            IncomingServerPort = "1",
            IncomingServerUsername = "user",
            IncomingServerPassword = "password",
            IncomingServerSocketOption = ImapConnectionSecurity.None,
            IncomingAuthenticationMethod = ImapAuthenticationMethod.Auto,
            MaxConcurrentClients = 2
        };

        using var pool = new ImapClientPool(ImapClientPoolOptions.CreateTestPool(serverInformation, protocolLog: null));

        var init1 = pool.InitializeAsync();
        var init2 = pool.InitializeAsync();

        await Task.WhenAll(init1, init2);
    }
}
