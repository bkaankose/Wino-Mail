using System;
using System.Linq;
using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Misc;
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
        ColorHelpers.GetFlatColorPalette().Should().Contain(calendars[0].BackgroundColorHex);
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

    [Fact]
    public async Task CreateAccountAsync_ImapLocalOnly_AssignsDistinctCalendarColorsAcrossAccounts()
    {
        var firstAccountId = Guid.NewGuid();
        var secondAccountId = Guid.NewGuid();

        await _accountService.CreateAccountAsync(
            CreateImapAccount(firstAccountId, "IMAP Test Account 1", "imap1@test.local"),
            new CustomServerInformation
            {
                Id = Guid.NewGuid(),
                AccountId = firstAccountId,
                CalendarSupportMode = ImapCalendarSupportMode.LocalOnly
            });

        await _accountService.CreateAccountAsync(
            CreateImapAccount(secondAccountId, "IMAP Test Account 2", "imap2@test.local"),
            new CustomServerInformation
            {
                Id = Guid.NewGuid(),
                AccountId = secondAccountId,
                CalendarSupportMode = ImapCalendarSupportMode.LocalOnly
            });

        var calendars = await _databaseService.Connection.Table<Wino.Core.Domain.Entities.Calendar.AccountCalendar>()
            .OrderBy(a => a.AccountId)
            .ToListAsync();

        calendars.Should().HaveCount(2);
        calendars.Select(a => a.BackgroundColorHex).Should().OnlyHaveUniqueItems();
        calendars.Should().OnlyContain(a => ColorHelpers.GetFlatColorPalette().Contains(a.BackgroundColorHex));
    }

    [Fact]
    public void FlatCalendarPalette_ProvidesAtLeastFiftyDistinctColors()
    {
        ColorHelpers.GetFlatColorPalette()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .Should()
            .BeGreaterThanOrEqualTo(50);
    }

    private static MailAccount CreateImapAccount(Guid accountId, string name = "IMAP Test Account", string address = "imap@test.local")
    {
        return new MailAccount
        {
            Id = accountId,
            Name = name,
            Address = address,
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
        var contactPictureFileService = new Mock<IContactPictureFileService>();

        var preferencesService = new Mock<IPreferencesService>();
        preferencesService.SetupProperty(a => a.StartupEntityId);

        return new AccountService(
            databaseService,
            signatureService.Object,
            authenticationProvider.Object,
            mimeFileService.Object,
            preferencesService.Object,
            contactPictureFileService.Object);
    }
}
