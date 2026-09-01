using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Xunit;

namespace Wino.Core.Tests.Providers;

public sealed class ExchangeProviderDetailTests
{
    [Fact]
    public void ExchangeProviderDetail_IsSupported_AndNamed()
    {
        var detail = new ProviderDetail(MailProviderType.Exchange, SpecialImapProvider.None);

        detail.Type.Should().Be(MailProviderType.Exchange);
        detail.Name.Should().Be("Exchange");
        detail.IsSupported.Should().BeTrue();
        detail.ProviderImage.Should().Contain("Exchange");
    }
}
