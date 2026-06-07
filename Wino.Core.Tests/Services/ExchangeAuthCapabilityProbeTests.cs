using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Core.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class ExchangeAuthCapabilityProbeTests
{
    [Fact]
    public void Classify_BearerPresent_IsModernAuthAvailable()
    {
        // Mirrors the live reactive challenge observed from on-prem Exchange (ADFS auth server).
        var challenges = new[]
        {
            "Bearer client_id=\"00000002-0000-0ff1-ce00-000000000000\", token_types=\"app_asserted_user_v1 service_asserted_app_v1\", error=\"invalid_token\"",
            "Negotiate",
            "NTLM",
            "Basic realm=\"ex01.mtec360.com\""
        };

        ExchangeAuthCapabilityProbe.ClassifyChallenges(challenges)
            .Should().Be(ExchangeAuthCapability.ModernAuthAvailable);
    }

    [Fact]
    public void Classify_LegacyOnly_IsBasicOnly()
    {
        var challenges = new[] { "Negotiate", "NTLM", "Basic realm=\"mail.example.com\"" };

        ExchangeAuthCapabilityProbe.ClassifyChallenges(challenges)
            .Should().Be(ExchangeAuthCapability.BasicOnly);
    }

    [Fact]
    public void Classify_BearerCaseInsensitiveAndPadded()
    {
        ExchangeAuthCapabilityProbe.ClassifyChallenges(new[] { "  bearer authorization_uri=\"x\"" })
            .Should().Be(ExchangeAuthCapability.ModernAuthAvailable);
    }

    [Fact]
    public void Classify_NoChallenges_IsUnknown()
    {
        ExchangeAuthCapabilityProbe.ClassifyChallenges(System.Array.Empty<string>())
            .Should().Be(ExchangeAuthCapability.Unknown);

        ExchangeAuthCapabilityProbe.ClassifyChallenges(null)
            .Should().Be(ExchangeAuthCapability.Unknown);
    }
}
