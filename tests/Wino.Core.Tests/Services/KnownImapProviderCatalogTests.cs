using System.Text;
using FluentAssertions;
using Wino.Core.Domain.Enums;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class KnownImapProviderCatalogTests
{
    private static EmbeddedKnownImapProviderCatalog CreateCatalog()
        => new(new KnownImapProviderCatalogLoader());

    [Fact]
    public void EmbeddedCatalog_IsValidAndVersioned()
    {
        var catalog = CreateCatalog();

        catalog.SchemaVersion.Should().Be(1);
        catalog.SetupProviders.Select(provider => provider.Id).Should().Equal("icloud", "yahoo");
    }

    [Theory]
    [InlineData("person@icloud.com", SpecialImapProvider.iCloud)]
    [InlineData("person@me.com", SpecialImapProvider.iCloud)]
    [InlineData("person@mac.com", SpecialImapProvider.iCloud)]
    [InlineData("person@yahoo.co.uk", SpecialImapProvider.Yahoo)]
    [InlineData("person@ymail.com", SpecialImapProvider.Yahoo)]
    public void Match_ResolvesKnownEmailDomains(string address, SpecialImapProvider expected)
        => CreateCatalog().Match(address, null)!.SpecialImapProvider.Should().Be(expected);

    [Theory]
    [InlineData("IMAP.MAIL.ME.COM", SpecialImapProvider.iCloud)]
    [InlineData("imap.mail.yahoo.com", SpecialImapProvider.Yahoo)]
    public void Match_ResolvesIncomingHosts(string host, SpecialImapProvider expected)
        => CreateCatalog().Match(null, host)!.SpecialImapProvider.Should().Be(expected);

    [Fact]
    public void ICloud_ContainsOfficialSettingsAndUsernamePolicies()
    {
        var catalog = CreateCatalog();
        var provider = catalog.GetBySpecialProvider(SpecialImapProvider.iCloud)!;

        provider.Incoming.Host.Should().Be("imap.mail.me.com");
        provider.Incoming.Port.Should().Be(993);
        provider.Incoming.Security.Should().Be(ImapConnectionSecurity.SslTls);
        provider.Outgoing.Host.Should().Be("smtp.mail.me.com");
        provider.Outgoing.Port.Should().Be(587);
        provider.Outgoing.Security.Should().Be(ImapConnectionSecurity.StartTls);
        catalog.ResolveUsername(provider.Incoming.UsernamePolicy, "person@icloud.com").Should().Be("person");
        catalog.ResolveUsername(provider.Outgoing.UsernamePolicy, "person@icloud.com").Should().Be("person@icloud.com");
        provider.CalDavServiceUrl.Should().Be("https://caldav.icloud.com/");
        provider.CardDavServiceUrl.Should().Be("https://contacts.icloud.com/");
        provider.AppPasswordHelpUrl.Should().StartWith("https://");
    }

    [Fact]
    public void Yahoo_ContainsOfficialSettingsAndFullAddressPolicies()
    {
        var catalog = CreateCatalog();
        var provider = catalog.GetBySpecialProvider(SpecialImapProvider.Yahoo)!;

        provider.Incoming.Host.Should().Be("imap.mail.yahoo.com");
        provider.Incoming.Port.Should().Be(993);
        provider.Incoming.Security.Should().Be(ImapConnectionSecurity.SslTls);
        provider.Outgoing.Host.Should().Be("smtp.mail.yahoo.com");
        provider.Outgoing.Port.Should().Be(587);
        provider.Outgoing.Security.Should().Be(ImapConnectionSecurity.StartTls);
        catalog.ResolveUsername(provider.Incoming.UsernamePolicy, "person@yahoo.com").Should().Be("person@yahoo.com");
        catalog.ResolveUsername(provider.Outgoing.UsernamePolicy, "person@yahoo.com").Should().Be("person@yahoo.com");
        provider.CalDavServiceUrl.Should().Be("https://caldav.calendar.yahoo.com/");
        provider.AppPasswordHelpUrl.Should().StartWith("https://");
    }

    [Fact]
    public void Loader_RejectsDuplicateProviderIds()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "providers": [
                { "id":"same", "specialImapProvider":"iCloud", "emailDomains":["a.test"], "incomingHosts":["imap.a.test"], "incoming":{"host":"imap.a.test","port":993,"security":"Auto","authentication":"Auto","usernamePolicy":"FullAddress"}, "outgoing":{"host":"smtp.a.test","port":587,"security":"Auto","authentication":"Auto","usernamePolicy":"FullAddress"}, "maxConcurrentClients":5, "folderAliases":[] },
                { "id":"same", "specialImapProvider":"Yahoo", "emailDomains":["b.test"], "incomingHosts":["imap.b.test"], "incoming":{"host":"imap.b.test","port":993,"security":"Auto","authentication":"Auto","usernamePolicy":"FullAddress"}, "outgoing":{"host":"smtp.b.test","port":587,"security":"Auto","authentication":"Auto","usernamePolicy":"FullAddress"}, "maxConcurrentClients":5, "folderAliases":[] }
              ],
              "genericFolderAliases": []
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var action = () => new KnownImapProviderCatalogLoader().Load(stream);

        action.Should().Throw<InvalidDataException>().WithMessage("*duplicated*");
    }

    [Fact]
    public void Loader_RejectsUnsupportedSchema()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"schemaVersion\":99}"));

        var action = () => new KnownImapProviderCatalogLoader().Load(stream);

        action.Should().Throw<InvalidDataException>().WithMessage("Unsupported*99*");
    }
}
