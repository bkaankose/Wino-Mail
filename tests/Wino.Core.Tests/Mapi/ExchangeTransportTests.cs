using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Xunit;

namespace Wino.Core.Tests.Mapi;

/// <summary>
/// The transport choice the synchronizer factory acts on: an explicit setting wins, detection decides
/// under Automatic, and an undecided account tries MAPI/HTTP first so the MAPI path can fall back itself.
/// </summary>
public class ExchangeTransportTests
{
    [Theory]
    [InlineData(ExchangeTransport.Automatic, ExchangeTransport.Automatic, ExchangeTransport.MapiHttp)]
    [InlineData(ExchangeTransport.Automatic, ExchangeTransport.MapiHttp, ExchangeTransport.MapiHttp)]
    [InlineData(ExchangeTransport.Automatic, ExchangeTransport.Ews, ExchangeTransport.Ews)]
    [InlineData(ExchangeTransport.MapiHttp, ExchangeTransport.Ews, ExchangeTransport.MapiHttp)]
    [InlineData(ExchangeTransport.Ews, ExchangeTransport.MapiHttp, ExchangeTransport.Ews)]
    [InlineData(ExchangeTransport.Ews, ExchangeTransport.Automatic, ExchangeTransport.Ews)]
    public void EffectiveTransport_PrefersTheExplicitChoice_ThenDetection_ThenMapi(ExchangeTransport chosen, ExchangeTransport detected, ExchangeTransport expected)
    {
        var server = new CustomServerInformation { ExchangeTransport = chosen, DetectedExchangeTransport = detected };

        server.EffectiveExchangeTransport.Should().Be(expected);
    }

    [Fact]
    public void Exchange_IsACustomServerAccount_AndAProviderMailAccount()
    {
        MailProviderType.Exchange.IsCustomMailProvider().Should().BeTrue("credentials live in CustomServerInformation");
        MailProviderType.Exchange.IsProviderMailAccount().Should().BeTrue("the server owns folders and message state");
        MailProviderType.Exchange.SupportsRemoteFolderSynchronization().Should().BeTrue();
        MailProviderType.Exchange.SupportsPushSynchronization().Should().BeTrue();
        MailProviderType.Exchange.UsesLocalMailState().Should().BeFalse();
        ((int)MailProviderType.Exchange).Should().Be(6, "2 and 3 were removed after a release and must not be reused");
    }
}
