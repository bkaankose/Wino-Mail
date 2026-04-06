using FluentAssertions;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Misc;
using Xunit;

namespace Wino.Core.Tests.Services;

public class MailHeaderExtensionsTests
{
    [Fact]
    public void MessageIdGenerator_Generate_ReturnsGuidAtWinoMailDomain()
    {
        var generated = MessageIdGenerator.Generate();

        generated.Should().MatchRegex("^<[0-9a-fA-F-]{36}@wino-mail\\.app>$");
    }

    [Fact]
    public void BuildReferencesChain_DeduplicatesAndAppendsParentMessageId()
    {
        var chain = MailHeaderExtensions.BuildReferencesChain(
            ["<root@domain.com>", "middle@domain.com", "<middle@domain.com>"],
            "<parent@domain.com>");

        chain.Should().Equal("root@domain.com", "middle@domain.com", "parent@domain.com");
    }
}
