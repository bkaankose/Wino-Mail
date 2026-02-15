using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class AccountServiceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private AccountService _accountService = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _accountService = CreateService(_databaseService);
    }

    public async Task DisposeAsync()
    {
        await _databaseService.DisposeAsync();
    }

    [Fact]
    public async Task CreateAccountAsync_ImapLocalOnly_CreatesSinglePrimaryDefaultCalendar()
    {
        var accountId = Guid.NewGuid();
        var account = CreateImapAccount(accountId);
        var server = new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            CalendarSupportMode = ImapCalendarSupportMode.LocalOnly
        };

        await _accountService.CreateAccountAsync(account, server);

        var calendars = await _databaseService.Connection.Table<Wino.Core.Domain.Entities.Calendar.AccountCalendar>()
            .Where(a => a.AccountId == accountId)
            .ToListAsync();

        calendars.Should().HaveCount(1);
        calendars[0].IsPrimary.Should().BeTrue();
        calendars[0].Name.Should().Be(Translator.AccountDetailsPage_TabCalendar);
    }

    [Fact]
    public async Task CreateAccountAsync_ImapCalDav_DoesNotCreateDefaultLocalCalendar()
    {
        var accountId = Guid.NewGuid();
        var account = CreateImapAccount(accountId);
        var server = new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            CalendarSupportMode = ImapCalendarSupportMode.CalDav
        };

        await _accountService.CreateAccountAsync(account, server);

        var calendars = await _databaseService.Connection.Table<Wino.Core.Domain.Entities.Calendar.AccountCalendar>()
            .Where(a => a.AccountId == accountId)
            .ToListAsync();

        calendars.Should().BeEmpty();
    }

    private static MailAccount CreateImapAccount(Guid accountId)
    {
        return new MailAccount
        {
            Id = accountId,
            Name = "IMAP Test Account",
            Address = "imap@test.local",
            SenderName = "IMAP Test",
            ProviderType = MailProviderType.IMAP4
        };
    }

    private static AccountService CreateService(InMemoryDatabaseService databaseService)
    {
        var signatureService = new Mock<ISignatureService>();
        signatureService
            .Setup(a => a.CreateDefaultSignatureAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid accountId) => new AccountSignature
            {
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                Name = "Default",
                HtmlBody = string.Empty
            });

        var authenticationProvider = new Mock<IAuthenticationProvider>();
        var mimeFileService = new Mock<IMimeFileService>();

        var preferencesService = new Mock<IPreferencesService>();
        preferencesService.SetupProperty(a => a.StartupEntityId);

        return new AccountService(
            databaseService,
            signatureService.Object,
            authenticationProvider.Object,
            mimeFileService.Object,
            preferencesService.Object);
    }
}
