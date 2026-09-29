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

        // Featured tiles come first in their hand-picked order; the rest of the catalog reads alphabetically.
        var setupIds = catalog.SetupProviders.Select(provider => provider.Id).ToList();
        setupIds.Take(2).Should().Equal("icloud", "yahoo");
        var catalogNames = catalog.SetupProviders.Where(provider => !provider.SetupFeatured).Select(provider => provider.DisplayName).ToList();
        catalogNames.Should().BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
        catalogNames.Should().HaveCountGreaterThan(20);
    }

    [Fact]
    public void EmbeddedCatalog_EverySetupProviderHasNameAndImageEnumValue()
    {
        foreach (var provider in CreateCatalog().SetupProviders)
        {
            provider.DisplayName.Should().NotBeNullOrWhiteSpace(provider.Id);
            Enum.IsDefined(provider.SpecialImapProvider).Should().BeTrue(provider.Id);
            provider.SpecialImapProvider.Should().NotBe(SpecialImapProvider.None, provider.Id);
        }
    }

    [Theory]
    [InlineData("person@gmx.de", SpecialImapProvider.Gmx)]
    [InlineData("person@proton.me", SpecialImapProvider.Proton)]
    [InlineData("person@fastmail.com", SpecialImapProvider.Fastmail)]
    [InlineData("person@zohomail.eu", SpecialImapProvider.Zoho)]
    public void Match_ResolvesCatalogProviders(string address, SpecialImapProvider expected)
        => CreateCatalog().Match(address, null)!.SpecialImapProvider.Should().Be(expected);

    [Fact]
    public void Zoho_RegionsSwapHostsAndAreListedAsMatchers()
    {
        var catalog = CreateCatalog();
        var provider = catalog.GetBySpecialProvider(SpecialImapProvider.Zoho)!;

        provider.Regions.Should().HaveCount(4);
        provider.Regions[0].Id.Should().Be("us");
        provider.Regions.Select(region => region.IncomingHost).Should().OnlyContain(host => provider.IncomingHosts.Contains(host));
        catalog.Match(null, "imap.zoho.eu")!.SpecialImapProvider.Should().Be(SpecialImapProvider.Zoho);
    }

    [Fact]
    public void Proton_UsesTheLocalBridge()
    {
        var provider = CreateCatalog().GetBySpecialProvider(SpecialImapProvider.Proton)!;

        provider.Incoming.Host.Should().Be("127.0.0.1");
        provider.Incoming.Port.Should().Be(1143);
        provider.Outgoing.Port.Should().Be(1025);
        provider.PasswordKind.Should().Be(KnownImapPasswordKind.BridgePassword);
        provider.SetupHint.Should().Be(KnownImapSetupHint.LocalBridgeRequired);
    }

    [Fact]
    public void Loader_RejectsRegionWhoseHostIsNotAMatcher()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "providers": [
                { "id":"zoho", "specialImapProvider":"Zoho", "displayName":"Zoho", "emailDomains":["a.test"], "incomingHosts":["imap.a.test"], "incoming":{"host":"imap.a.test","port":993,"security":"Auto","authentication":"Auto","usernamePolicy":"FullAddress"}, "outgoing":{"host":"smtp.a.test","port":587,"security":"Auto","authentication":"Auto","usernamePolicy":"FullAddress"}, "maxConcurrentClients":5, "folderAliases":[],
                  "regions":[ { "id":"eu", "displayName":"Europe", "incomingHost":"imap.eu.test", "outgoingHost":"smtp.eu.test" } ] }
              ],
              "genericFolderAliases": []
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var action = () => new KnownImapProviderCatalogLoader().Load(stream);

        action.Should().Throw<InvalidDataException>().WithMessage("*region 'eu'*matcher*");
    }

    [Fact]
    public void Loader_RejectsVisibleProviderWithoutDisplayName()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "providers": [
                { "id":"gmx", "specialImapProvider":"Gmx", "setupVisible":true, "emailDomains":["a.test"], "incomingHosts":["imap.a.test"], "incoming":{"host":"imap.a.test","port":993,"security":"Auto","authentication":"Auto","usernamePolicy":"FullAddress"}, "outgoing":{"host":"smtp.a.test","port":587,"security":"Auto","authentication":"Auto","usernamePolicy":"FullAddress"}, "maxConcurrentClients":5, "folderAliases":[] }
              ],
              "genericFolderAliases": []
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var action = () => new KnownImapProviderCatalogLoader().Load(stream);

        action.Should().Throw<InvalidDataException>().WithMessage("*display name*");
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

    [Theory]
    [InlineData("person@icloud.com", "iCloud", "https://support.apple.com/102654")]
    [InlineData("person@ymail.com", "Yahoo Mail", "https://help.yahoo.com/kb/generate-manage-third-party-passwords-sln15241.html")]
    [InlineData("person@FASTMAIL.com", "Fastmail", "https://www.fastmail.help/hc/en-us/articles/360058752854-App-passwords")]
    [InlineData("person@aol.com", "AOL", "https://help.aol.com/articles/create-and-manage-app-password")]
    public void FindAppPasswordHelp_ResolvesProviders(string address, string providerName, string helpUrl)
    {
        var help = CreateCatalog().FindAppPasswordHelp(address);

        help.Should().NotBeNull();
        help!.ProviderName.Should().Be(providerName);
        help.HelpUrl.Should().Be(helpUrl);
    }

    [Theory]
    [InlineData("person@example.org")]
    [InlineData("not-an-address")]
    [InlineData("")]
    [InlineData(null)]
    public void FindAppPasswordHelp_ReturnsNullWithoutKnownDomain(string? address)
        => CreateCatalog().FindAppPasswordHelp(address!).Should().BeNull();

    [Fact]
    public void HelpOnlyEntries_DoNotBecomeSetupProviders()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "providers": [],
              "genericFolderAliases": [],
              "appPasswordHelp": [
                { "id":"other", "displayName":"Other", "emailDomains":["other.test"], "helpUrl":"https://help.other.test/" }
              ]
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var catalog = new KnownImapProviderCatalog(new KnownImapProviderCatalogLoader().Load(stream));

        catalog.Match("person@other.test", null).Should().BeNull();
        catalog.SetupProviders.Should().BeEmpty();
        catalog.FindAppPasswordHelp("person@other.test")!.ProviderName.Should().Be("Other");
    }

    [Fact]
    public void Loader_RejectsHelpDomainOwnedByProvider()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "providers": [
                { "id":"icloud", "specialImapProvider":"iCloud", "emailDomains":["a.test"], "incomingHosts":["imap.a.test"], "incoming":{"host":"imap.a.test","port":993,"security":"Auto","authentication":"Auto","usernamePolicy":"FullAddress"}, "outgoing":{"host":"smtp.a.test","port":587,"security":"Auto","authentication":"Auto","usernamePolicy":"FullAddress"}, "maxConcurrentClients":5, "folderAliases":[] }
              ],
              "genericFolderAliases": [],
              "appPasswordHelp": [
                { "id":"other", "displayName":"Other", "emailDomains":["A.test"], "helpUrl":"https://help.a.test/" }
              ]
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var action = () => new KnownImapProviderCatalogLoader().Load(stream);

        action.Should().Throw<InvalidDataException>().WithMessage("*duplicated email domain*");
    }

    [Fact]
    public void Loader_RejectsHelpEntryWithoutAbsoluteUrl()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "providers": [],
              "genericFolderAliases": [],
              "appPasswordHelp": [
                { "id":"other", "displayName":"Other", "emailDomains":["b.test"], "helpUrl":"help/app-passwords" }
              ]
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var action = () => new KnownImapProviderCatalogLoader().Load(stream);

        action.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Loader_AcceptsCatalogWithoutHelpList()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""{ "schemaVersion": 1, "providers": [], "genericFolderAliases": [] }"""));

        var document = new KnownImapProviderCatalogLoader().Load(stream);

        document.AppPasswordHelp.Should().BeEmpty();
    }

    [Fact]
    public void Loader_RejectsUnsupportedSchema()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"schemaVersion\":99}"));

        var action = () => new KnownImapProviderCatalogLoader().Load(stream);

        action.Should().Throw<InvalidDataException>().WithMessage("Unsupported*99*");
    }
}
