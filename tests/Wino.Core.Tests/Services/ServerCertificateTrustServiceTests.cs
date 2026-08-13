using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class ServerCertificateTrustServiceTests
{
    [Fact]
    public async Task Trust_IsEndpointAndProtocolScoped_AndReplacementIsAtomicPerEndpoint()
    {
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        var service = new ServerCertificateTrustService(database);
        var accountId = Guid.NewGuid();

        await service.SaveTrustsAsync(accountId,
        [
            CreateTrust(accountId, MailServerProtocol.Imap, "MAIL.Example.COM", 993, "AA"),
            CreateTrust(accountId, MailServerProtocol.Smtp, "mail.example.com", 993, "BB")
        ]);

        (await service.GetTrustAsync(accountId, MailServerProtocol.Imap, "mail.example.com", 993))!
            .CertificateSha256.Should().Be("AA");
        (await service.GetTrustAsync(accountId, MailServerProtocol.Smtp, "mail.example.com", 993))!
            .CertificateSha256.Should().Be("BB");

        await service.SaveTrustsAsync(accountId,
        [
            CreateTrust(accountId, MailServerProtocol.Imap, "mail.example.com", 993, "CC")
        ]);

        var rows = await database.Connection.Table<MailServerCertificateTrust>().ToListAsync();
        rows.Should().HaveCount(2);
        (await service.GetTrustAsync(accountId, MailServerProtocol.Imap, "mail.example.com", 993))!
            .CertificateSha256.Should().Be("CC");
    }

    [Fact]
    public async Task DeleteAccountTrusts_DoesNotDeleteAnotherAccountsTrust()
    {
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        var service = new ServerCertificateTrustService(database);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await service.SaveTrustsAsync(first, [CreateTrust(first, MailServerProtocol.Imap, "imap.example.com", 993, "AA")]);
        await service.SaveTrustsAsync(second, [CreateTrust(second, MailServerProtocol.Imap, "imap.example.com", 993, "BB")]);

        await service.DeleteAccountTrustsAsync(first);

        (await service.GetTrustAsync(first, MailServerProtocol.Imap, "imap.example.com", 993)).Should().BeNull();
        (await service.GetTrustAsync(second, MailServerProtocol.Imap, "imap.example.com", 993)).Should().NotBeNull();
    }

    private static MailServerCertificateTrust CreateTrust(
        Guid accountId,
        MailServerProtocol protocol,
        string host,
        int port,
        string fingerprint)
        => new()
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Protocol = protocol,
            Host = host,
            Port = port,
            CertificateSha256 = fingerprint,
            CertificateRawData = [1, 2, 3],
            Subject = "CN=mail.example.com",
            Issuer = "CN=Test CA",
            ValidFromUtc = DateTime.UtcNow.AddDays(-1),
            ValidToUtc = DateTime.UtcNow.AddDays(1),
            AcceptedAtUtc = DateTime.UtcNow
        };
}
