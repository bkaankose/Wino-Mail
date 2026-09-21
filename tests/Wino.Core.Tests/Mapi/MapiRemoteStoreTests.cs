using FluentAssertions;
using Wino.Core.Synchronizers.Mapi;
using Xunit;

namespace Wino.Core.Tests.Mapi;

public class MapiRemoteStoreTests
{
    [Fact]
    public void ReplicaToAddress_ExtractsTheContentMailboxRoutingAddress()
    {
        const string replica = "/o=First Organization/ou=Exchange Administrative Group (FYDIBOHF23SPDLT)/cn=Configuration/cn=Servers/cn=1c2b9f4e-2d3a-4a1b-9d9e-5f6a7b8c9d0e@contoso.com/cn=Microsoft Public MDB";

        MapiExchangeSynchronizer.ReplicaToAddress(replica).Should().Be("1c2b9f4e-2d3a-4a1b-9d9e-5f6a7b8c9d0e@contoso.com");
    }

    [Fact]
    public void ReplicaToAddress_PassesOtherValuesThrough()
    {
        MapiExchangeSynchronizer.ReplicaToAddress("publicfolders@contoso.com").Should().Be("publicfolders@contoso.com");
    }
}
