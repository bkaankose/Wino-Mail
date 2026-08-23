using System;
using System.Linq;
using FluentAssertions;
using Moq;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Misc;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class AccountServiceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private AccountService _accountService = null!;
    private Mock<IAccountProfilePictureFileService> _profilePictureFileService = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();
        _profilePictureFileService = new Mock<IAccountProfilePictureFileService>();
        _accountService = CreateService(_databaseService, _profilePictureFileService.Object);
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
    public async Task CreateAccountAsync_DefaultsToAppendingSentMessages()
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

        var preferences = await _databaseService.Connection
            .Table<MailAccountPreferences>()
            .FirstAsync(item => item.AccountId == accountId);

        preferences.ShouldAppendMessagesToSentFolder.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAccountAsync_UsesRequestedSentMessageAppendPreference()
    {
        var accountId = Guid.NewGuid();
        var account = CreateImapAccount(accountId, "No sent append", "no-append@test.local");
        var server = new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            CalendarSupportMode = ImapCalendarSupportMode.LocalOnly
        };

        await _accountService.CreateAccountAsync(account, server, shouldAppendMessagesToSentFolder: false);

        var preferences = await _databaseService.Connection
            .Table<MailAccountPreferences>()
            .FirstAsync(item => item.AccountId == accountId);

        preferences.ShouldAppendMessagesToSentFolder.Should().BeFalse();
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

    [Fact]
    public async Task UpdateProfileInformationAsync_DownloadedPicture_UsesAccountOwnedStorageWithoutCreatingContact()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Gmail",
            Address = "profile@test.local",
            SenderName = "Old name",
            ProviderType = MailProviderType.Gmail
        };
        var newFileId = Guid.NewGuid();
        var imageData = new byte[] { 1, 2, 3 };
        await _databaseService.Connection.InsertAsync(account);
        _profilePictureFileService
            .Setup(service => service.SaveProfilePictureAsync(imageData, null, default))
            .ReturnsAsync(newFileId);

        await _accountService.UpdateProfileInformationAsync(
            account.Id,
            new ProfileInformation("New name", ProfilePictureFetchResult.Downloaded(imageData), account.Address));

        var updated = await _databaseService.Connection.FindAsync<MailAccount>(account.Id);
        updated.ProfilePictureFileId.Should().Be(newFileId);
        updated.IsProfilePictureBackfillComplete.Should().BeTrue();
        (await _databaseService.Connection.Table<AccountContact>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task UpdateProfileInformationAsync_FetchFailed_PreservesManualPictureAndBackfillState()
    {
        var manualFileId = Guid.NewGuid();
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Outlook",
            Address = "profile@test.local",
            ProviderType = MailProviderType.Outlook,
            ProfilePictureFileId = manualFileId,
            IsProfilePictureBackfillComplete = false
        };
        await _databaseService.Connection.InsertAsync(account);

        await _accountService.UpdateProfileInformationAsync(
            account.Id,
            new ProfileInformation("Name", ProfilePictureFetchResult.FetchFailed, account.Address));

        var updated = await _databaseService.Connection.FindAsync<MailAccount>(account.Id);
        updated.ProfilePictureFileId.Should().Be(manualFileId);
        updated.IsProfilePictureBackfillComplete.Should().BeFalse();
        _profilePictureFileService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UpdateProfileInformationAsync_ExplicitConfirmedAbsent_RemovesCurrentPicture()
    {
        var currentFileId = Guid.NewGuid();
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Outlook",
            Address = "profile@test.local",
            ProviderType = MailProviderType.Outlook,
            ProfilePictureFileId = currentFileId
        };
        await _databaseService.Connection.InsertAsync(account);

        await _accountService.UpdateProfileInformationAsync(
            account.Id,
            new ProfileInformation("Name", ProfilePictureFetchResult.ConfirmedAbsent, account.Address),
            removePictureWhenConfirmedAbsent: true);

        var updated = await _databaseService.Connection.FindAsync<MailAccount>(account.Id);
        updated.ProfilePictureFileId.Should().BeNull();
        updated.IsProfilePictureBackfillComplete.Should().BeTrue();
        _profilePictureFileService.Verify(
            service => service.DeleteProfilePictureAsync(currentFileId),
            Times.Once);
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

    private static AccountService CreateService(
        InMemoryDatabaseService databaseService,
        IAccountProfilePictureFileService accountProfilePictureFileService = null)
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
            contactPictureFileService.Object,
            accountProfilePictureFileService: accountProfilePictureFileService);
    }
}
