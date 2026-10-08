using FluentAssertions;
using Wino.Core.Activation;
using Xunit;

namespace Wino.Core.Tests;

public class WinoProtocolActivationResolverTests
{
    [Theory]
    [InlineData("wino://billing/success")]
    [InlineData("WINO://BILLING/SUCCESS")]
    public void IsBillingSuccess_AcceptsExactDeepLink(string value)
    {
        WinoProtocolActivationResolver.IsBillingSuccess(new Uri(value)).Should().BeTrue();
    }

    [Theory]
    [InlineData("wino://billing")]
    [InlineData("wino://billing/success/extra")]
    [InlineData("wino://billing/success?session_id=secret")]
    [InlineData("wino://mail/success")]
    [InlineData("https://billing/success")]
    public void IsBillingSuccess_RejectsOtherUris(string value)
    {
        WinoProtocolActivationResolver.IsBillingSuccess(new Uri(value)).Should().BeFalse();
    }

}
