using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class SpecialImapProviderConfigResolverTests
{
    [Fact]
    public void GetServerInformation_ICloud_UsesOfficialIncomingAndOutgoingUsernamePolicies()
    {
        var sut = CreateSut();
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Address = "tester@icloud.com"
        };
        var dialogResult = new AccountCreationDialogResult(
            MailProviderType.IMAP4,
            "iCloud",
            new SpecialImapProviderDetails(
                "tester@icloud.com",
                "app-password",
                "Tester",
                SpecialImapProvider.iCloud,
                ImapCalendarSupportMode.CalDav),
            "#0078D4",
            InitialSynchronizationRange.SixMonths,
            true,
            true);

        var serverInformation = sut.GetServerInformation(account, dialogResult);

        serverInformation.IncomingServerUsername.Should().Be("tester");
        serverInformation.OutgoingServerUsername.Should().Be("tester@icloud.com");
        serverInformation.CalDavUsername.Should().Be("tester@icloud.com");
        serverInformation.CardDavServiceUrl.Should().Be("https://contacts.icloud.com/");
    }

    [Fact]
    public void GetServerInformation_CardDavWithoutCalendar_RetainsSharedDavCredentials()
    {
        var sut = CreateSut();
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Address = "tester@icloud.com",
            IsContactAccessGranted = true,
            ContactIntegrationSource = AccountIntegrationSource.Dav
        };
        var dialogResult = new AccountCreationDialogResult(
            MailProviderType.IMAP4,
            "iCloud",
            new SpecialImapProviderDetails(
                "tester@icloud.com",
                "app-password",
                "Tester",
                SpecialImapProvider.iCloud,
                ImapCalendarSupportMode.Disabled),
            "#0078D4",
            InitialSynchronizationRange.SixMonths,
            true,
            false);

        var serverInformation = sut.GetServerInformation(account, dialogResult);

        serverInformation.CalDavServiceUrl.Should().BeEmpty();
        serverInformation.CardDavServiceUrl.Should().Be("https://contacts.icloud.com/");
        serverInformation.CalDavUsername.Should().Be("tester@icloud.com");
        serverInformation.CalDavPassword.Should().Be("app-password");
    }

    [Theory]
    [InlineData(null, "imap.zoho.com", "smtp.zoho.com")]
    [InlineData("", "imap.zoho.com", "smtp.zoho.com")]
    [InlineData("EU", "imap.zoho.eu", "smtp.zoho.eu")]
    [InlineData("unknown-region", "imap.zoho.com", "smtp.zoho.com")]
    public void GetServerInformation_Zoho_UsesTheChosenRegionOrTheFirstOne(string? regionId, string incomingHost, string outgoingHost)
    {
        var sut = CreateSut();
        var account = new MailAccount { Id = Guid.NewGuid(), Address = "tester@zohomail.eu" };
        var dialogResult = new AccountCreationDialogResult(
            MailProviderType.IMAP4,
            "Zoho",
            new SpecialImapProviderDetails("tester@zohomail.eu", "app-password", "Tester", SpecialImapProvider.Zoho, ImapCalendarSupportMode.Disabled, regionId),
            "#0078D4",
            InitialSynchronizationRange.SixMonths,
            true,
            false);

        var serverInformation = sut.GetServerInformation(account, dialogResult);

        serverInformation.IncomingServer.Should().Be(incomingHost);
        serverInformation.OutgoingServer.Should().Be(outgoingHost);
        serverInformation.IncomingServerPort.Should().Be("993");
    }

    [Fact]
    public void GetServerInformation_Proton_PointsAtTheLocalBridge()
    {
        var sut = CreateSut();
        var account = new MailAccount { Id = Guid.NewGuid(), Address = "tester@proton.me" };
        var dialogResult = new AccountCreationDialogResult(
            MailProviderType.IMAP4,
            "Proton",
            new SpecialImapProviderDetails("tester@proton.me", "bridge-password", "Tester", SpecialImapProvider.Proton, ImapCalendarSupportMode.Disabled),
            "#0078D4",
            InitialSynchronizationRange.SixMonths,
            true,
            false);

        var serverInformation = sut.GetServerInformation(account, dialogResult);

        serverInformation.IncomingServer.Should().Be("127.0.0.1");
        serverInformation.IncomingServerPort.Should().Be("1143");
        serverInformation.IncomingServerSocketOption.Should().Be(ImapConnectionSecurity.StartTls);
        serverInformation.OutgoingServerPort.Should().Be("1025");
        serverInformation.CalDavServiceUrl.Should().BeEmpty();
    }

    private static SpecialImapProviderConfigResolver CreateSut()
        => new(new EmbeddedKnownImapProviderCatalog(new KnownImapProviderCatalogLoader()));
}
