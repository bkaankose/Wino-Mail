using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Services.PushRelay;
using Xunit;

namespace Wino.Core.Tests.Services.PushRelay;

public sealed class ImapPushRelayMailboxRegistrationBuilderTests
{
    [Theory]
    [InlineData(ImapConnectionSecurity.SslTls, "993", "ssl", 993)]
    [InlineData(ImapConnectionSecurity.StartTls, "143", "starttls", 143)]
    [InlineData(ImapConnectionSecurity.None, "143", "none", 143)]
    [InlineData(ImapConnectionSecurity.Auto, "993", "ssl", 993)]
    [InlineData(ImapConnectionSecurity.Auto, "143", "starttls", 143)]
    [InlineData(ImapConnectionSecurity.SslTls, "not-a-port", "ssl", 993)]
    [InlineData(ImapConnectionSecurity.StartTls, "", "starttls", 143)]
    public async Task MapsSecurityAndPort(ImapConnectionSecurity security, string port, string expectedSecurity, int expectedPort)
    {
        var account = Account();
        var builder = Builder(account, Server(account, security: security, port: port));

        var registration = await builder.BuildAsync(account);

        registration.Should().NotBeNull();
        var imap = registration!.Create.Imap!;
        imap.Security.Should().Be(expectedSecurity);
        imap.Port.Should().Be(expectedPort);
        imap.AuthMethod.Should().Be("password");
        registration.Create.Provider.Should().Be(PushRelayProviders.Imap);
        registration.Create.ClientAccountId.Should().Be(account.Id.ToString("D"));
        registration.Update.Imap.Should().Be(imap);
        registration.ToString().Should().NotContain("secret");
    }

    [Fact]
    public async Task FallsBackToAccountAddressForUsername()
    {
        var account = Account();
        var builder = Builder(account, Server(account, username: " "));

        (await builder.BuildAsync(account))!.Create.Imap!.Username.Should().Be(account.Address);
    }

    [Fact]
    public async Task IneligibleAccounts_ProduceNothing()
    {
        var notImap = Account(MailProviderType.Gmail);
        var noAccess = Account();
        noAccess.IsMailAccessGranted = false;
        var badPassword = Account();
        badPassword.AttentionReason = AccountAttentionReason.InvalidCredentials;
        var pop = Account();
        var noPassword = Account();
        var noHost = Account();

        (await Builder(notImap, Server(notImap)).BuildAsync(notImap)).Should().BeNull();
        (await Builder(noAccess, Server(noAccess)).BuildAsync(noAccess)).Should().BeNull();
        (await Builder(badPassword, Server(badPassword)).BuildAsync(badPassword)).Should().BeNull();
        (await Builder(pop, Server(pop, type: CustomIncomingServerType.POP3)).BuildAsync(pop)).Should().BeNull();
        (await Builder(noPassword, Server(noPassword, password: "")).BuildAsync(noPassword)).Should().BeNull();
        (await Builder(noHost, Server(noHost, host: " ")).BuildAsync(noHost)).Should().BeNull();
        (await Builder(noHost, null).IsEligibleAsync(noHost)).Should().BeFalse();
    }

    [Fact]
    public async Task Fingerprint_IsStableAndChangesWithCredentials()
    {
        var account = Account();
        var first = await Builder(account, Server(account)).BuildAsync(account);
        var again = await Builder(account, Server(account)).BuildAsync(account);
        var newPassword = await Builder(account, Server(account, password: "other")).BuildAsync(account);
        var newHost = await Builder(account, Server(account, host: "imap2.example.test")).BuildAsync(account);

        first!.CredentialFingerprint.Should().Be(again!.CredentialFingerprint).And.HaveLength(64);
        newPassword!.CredentialFingerprint.Should().NotBe(first.CredentialFingerprint);
        newHost!.CredentialFingerprint.Should().NotBe(first.CredentialFingerprint);
        first.CredentialFingerprint.Should().NotContain("secret");
    }

    private static MailAccount Account(MailProviderType type = MailProviderType.IMAP4)
        => new() { Id = Guid.NewGuid(), ProviderType = type, Address = "user@example.test", IsMailAccessGranted = true };

    private static CustomServerInformation Server(MailAccount account, ImapConnectionSecurity security = ImapConnectionSecurity.SslTls,
        string port = "993", string username = "user", string password = "secret", string host = "imap.example.test",
        CustomIncomingServerType type = CustomIncomingServerType.IMAP4)
        => new()
        {
            AccountId = account.Id,
            IncomingServer = host,
            IncomingServerPort = port,
            IncomingServerUsername = username,
            IncomingServerPassword = password,
            IncomingServerSocketOption = security,
            IncomingServerType = type
        };

    private static ImapPushRelayMailboxRegistrationBuilder Builder(MailAccount account, CustomServerInformation? server)
    {
        var accounts = new Mock<IAccountService>();
        accounts.Setup(x => x.GetAccountCustomServerInformationAsync(account.Id)).ReturnsAsync(server!);
        return new ImapPushRelayMailboxRegistrationBuilder(accounts.Object);
    }
}
