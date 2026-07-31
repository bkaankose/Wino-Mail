using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Tests.Helpers;
using Wino.Mail.Api.Contracts.Users;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class WinoAccountDataSyncServiceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private Mock<IWinoAccountProfileService> _profileService = null!;
    private Mock<IPreferencesService> _preferencesService = null!;
    private AccountService _accountService = null!;
    private WinoAccountDataSyncService _service = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();

        _profileService = new Mock<IWinoAccountProfileService>(MockBehavior.Strict);
        _preferencesService = new Mock<IPreferencesService>();
        _preferencesService.SetupProperty(a => a.StartupEntityId);

        _accountService = CreateAccountService(_databaseService, _preferencesService.Object);
        _service = new WinoAccountDataSyncService(_profileService.Object, _preferencesService.Object, _accountService);
    }

    public async Task DisposeAsync()
    {
        await _databaseService.DisposeAsync();
    }

    [Fact]
    public async Task ExportAsync_ImapMailbox_MapsSanitizedPayload()
    {
        var accountId = Guid.NewGuid();

        await _accountService.CreateAccountAsync(
            new MailAccount
            {
                Id = accountId,
                Name = "Custom IMAP",
                SenderName = "Custom IMAP Sender",
                Address = "imap@example.com",
                ProviderType = MailProviderType.IMAP4,
                SpecialImapProvider = SpecialImapProvider.iCloud,
                AccountColorHex = "#123456",
                IsCalendarAccessGranted = true,
                SynchronizationDeltaIdentifier = "delta-token",
                CalendarSynchronizationDeltaIdentifier = "calendar-delta",
                Base64ProfilePictureData = "profile"
            },
            new CustomServerInformation
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                Address = "imap@example.com",
                IncomingServer = "imap.example.com",
                IncomingServerPort = "993",
                IncomingServerUsername = "imap-user",
                IncomingServerPassword = "secret-incoming",
                IncomingServerSocketOption = ImapConnectionSecurity.Auto,
                IncomingAuthenticationMethod = ImapAuthenticationMethod.NormalPassword,
                OutgoingServer = "smtp.example.com",
                OutgoingServerPort = "465",
                OutgoingServerUsername = "smtp-user",
                OutgoingServerPassword = "secret-outgoing",
                OutgoingServerSocketOption = ImapConnectionSecurity.Auto,
                OutgoingAuthenticationMethod = ImapAuthenticationMethod.NormalPassword,
                CalendarSupportMode = ImapCalendarSupportMode.CalDav,
                CalDavServiceUrl = "https://dav.example.com",
                CalDavUsername = "dav-user",
                CalDavPassword = "secret-caldav",
                ProxyServer = "proxy.example.com",
                ProxyServerPort = "8080",
                MaxConcurrentClients = 7
            });

        ReplaceUserMailboxesRequestDto? capturedRequest = null;
        _profileService
            .Setup(a => a.ReplaceMailboxesAsync(It.IsAny<ReplaceUserMailboxesRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback<ReplaceUserMailboxesRequestDto, CancellationToken>((request, _) => capturedRequest = request)
            .Returns(Task.CompletedTask);

        var result = await _service.ExportAsync(new WinoAccountSyncSelection(IncludePreferences: false, IncludeAccounts: true));

        result.ExportedMailboxCount.Should().Be(1);
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Mailboxes.Should().ContainSingle();

        var exportedMailbox = capturedRequest.Mailboxes[0];
        exportedMailbox.Address.Should().Be("imap@example.com");
        exportedMailbox.ProviderType.Should().Be((int)MailProviderType.IMAP4);
        exportedMailbox.SpecialImapProvider.Should().Be((int)SpecialImapProvider.iCloud);
        exportedMailbox.AccountName.Should().Be("Custom IMAP");
        exportedMailbox.SenderName.Should().Be("Custom IMAP Sender");
        exportedMailbox.AccountColorHex.Should().Be("#123456");
        exportedMailbox.IsCalendarAccessGranted.Should().BeTrue();
        exportedMailbox.IncomingServer.Should().Be("imap.example.com");
        exportedMailbox.IncomingServerUsername.Should().Be("imap-user");
        exportedMailbox.OutgoingServer.Should().Be("smtp.example.com");
        exportedMailbox.OutgoingServerUsername.Should().Be("smtp-user");
        exportedMailbox.CalDavServiceUrl.Should().Be("https://dav.example.com");
        exportedMailbox.CalDavUsername.Should().Be("dav-user");
        exportedMailbox.ProxyServer.Should().Be("proxy.example.com");
        exportedMailbox.ProxyServerPort.Should().Be("8080");
        exportedMailbox.MaxConcurrentClients.Should().Be(7);

        _profileService.Verify(a => a.SaveSettingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExportAsync_GmailMailbox_DoesNotIncludeCustomServerSettings()
    {
        await _accountService.CreateAccountAsync(
            new MailAccount
            {
                Id = Guid.NewGuid(),
                Name = "Gmail",
                SenderName = "Gmail Sender",
                Address = "gmail@example.com",
                ProviderType = MailProviderType.Gmail
            },
            null!);

        ReplaceUserMailboxesRequestDto? capturedRequest = null;
        _profileService
            .Setup(a => a.ReplaceMailboxesAsync(It.IsAny<ReplaceUserMailboxesRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback<ReplaceUserMailboxesRequestDto, CancellationToken>((request, _) => capturedRequest = request)
            .Returns(Task.CompletedTask);

        await _service.ExportAsync(new WinoAccountSyncSelection(IncludePreferences: false, IncludeAccounts: true));

        var exportedMailbox = capturedRequest!.Mailboxes.Single();
        exportedMailbox.IncomingServer.Should().BeNull();
        exportedMailbox.OutgoingServer.Should().BeNull();
        exportedMailbox.CalDavServiceUrl.Should().BeNull();
        exportedMailbox.MaxConcurrentClients.Should().BeNull();
    }

    [Fact]
    public async Task ImportAsync_SkipsDuplicateMailbox_ByAddressAndProviderCaseInsensitive()
    {
        await _accountService.CreateAccountAsync(
            new MailAccount
            {
                Id = Guid.NewGuid(),
                Name = "Existing Gmail",
                SenderName = "Existing Gmail",
                Address = "User@Example.com",
                ProviderType = MailProviderType.Gmail
            },
            null!);

        _profileService
            .Setup(a => a.GetMailboxesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserMailboxSyncListDto(
            [
                new UserMailboxSyncItemDto
                {
                    Address = "user@example.com",
                    ProviderType = (int)MailProviderType.Gmail,
                    AccountName = "Duplicate Gmail"
                },
                new UserMailboxSyncItemDto
                {
                    Address = "second@example.com",
                    ProviderType = (int)MailProviderType.Outlook,
                    AccountName = "New Outlook"
                }
            ]));

        var result = await _service.ImportAsync(new WinoAccountSyncSelection(IncludePreferences: false, IncludeAccounts: true));

        result.ImportedMailboxCount.Should().Be(1);
        result.SkippedDuplicateMailboxCount.Should().Be(1);

        var accounts = await _accountService.GetAccountsAsync();
        accounts.Should().HaveCount(2);
        accounts.Should().Contain(a => a.Address == "second@example.com" && a.ProviderType == MailProviderType.Outlook);
    }

    [Fact]
    public async Task ImportAsync_ImapMailbox_CreatesRootAliasAndInvalidCredentialsAttentionWithoutPasswords()
    {
        _profileService
            .Setup(a => a.GetMailboxesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserMailboxSyncListDto(
            [
                new UserMailboxSyncItemDto
                {
                    Address = "imap@example.com",
                    ProviderType = (int)MailProviderType.IMAP4,
                    SpecialImapProvider = (int)SpecialImapProvider.Yahoo,
                    AccountName = "Imported IMAP",
                    SenderName = "Imported Sender",
                    CalendarSupportMode = (int)ImapCalendarSupportMode.CalDav,
                    IncomingServer = "imap.example.com",
                    IncomingServerPort = "993",
                    IncomingServerUsername = "imap-user",
                    IncomingServerSocketOption = (int)ImapConnectionSecurity.Auto,
                    IncomingAuthenticationMethod = (int)ImapAuthenticationMethod.NormalPassword,
                    OutgoingServer = "smtp.example.com",
                    OutgoingServerPort = "465",
                    OutgoingServerUsername = "smtp-user",
                    OutgoingServerSocketOption = (int)ImapConnectionSecurity.Auto,
                    OutgoingAuthenticationMethod = (int)ImapAuthenticationMethod.NormalPassword,
                    CalDavServiceUrl = "https://dav.example.com",
                    CalDavUsername = "dav-user",
                    MaxConcurrentClients = 9
                }
            ]));

        var result = await _service.ImportAsync(new WinoAccountSyncSelection(IncludePreferences: false, IncludeAccounts: true));

        result.ImportedMailboxCount.Should().Be(1);

        var importedAccount = (await _accountService.GetAccountsAsync()).Single();
        importedAccount.AttentionReason.Should().Be(AccountAttentionReason.InvalidCredentials);
        importedAccount.SynchronizationDeltaIdentifier.Should().BeEmpty();
        importedAccount.CalendarSynchronizationDeltaIdentifier.Should().BeEmpty();

        var importedAliases = await _accountService.GetAccountAliasesAsync(importedAccount.Id);
        importedAliases.Should().ContainSingle(a => a.IsRootAlias && a.IsPrimary && a.AliasAddress == "imap@example.com");

        var serverInformation = await _accountService.GetAccountCustomServerInformationAsync(importedAccount.Id);
        serverInformation.Should().NotBeNull();
        serverInformation.IncomingServerPassword.Should().BeEmpty();
        serverInformation.OutgoingServerPassword.Should().BeEmpty();
        serverInformation.CalDavPassword.Should().BeEmpty();
        serverInformation.MaxConcurrentClients.Should().Be(9);
        serverInformation.CalDavServiceUrl.Should().Be("https://dav.example.com");
    }

    private static AccountService CreateAccountService(InMemoryDatabaseService databaseService, IPreferencesService preferencesService)
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

        return new AccountService(
            databaseService,
            signatureService.Object,
            Mock.Of<IAuthenticationProvider>(),
            Mock.Of<IMimeFileService>(),
            preferencesService,
            Mock.Of<IContactPictureFileService>());
    }
}
