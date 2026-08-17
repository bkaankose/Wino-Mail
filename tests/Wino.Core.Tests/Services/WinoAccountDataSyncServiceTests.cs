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
    private FolderService _folderService = null!;
    private SignatureService _signatureService = null!;
    private WinoAccountDataSyncService _service = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();

        _profileService = new Mock<IWinoAccountProfileService>(MockBehavior.Strict);
        _preferencesService = new Mock<IPreferencesService>();
        _preferencesService.SetupProperty(a => a.StartupEntityId);

        _accountService = CreateAccountService(_databaseService, _preferencesService.Object);
        _folderService = new FolderService(_databaseService, _accountService, new MailCategoryService(_databaseService));
        _signatureService = new SignatureService(_databaseService);
        _service = new WinoAccountDataSyncService(
            _profileService.Object,
            _preferencesService.Object,
            _accountService,
            _folderService,
            _signatureService);
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
        serverInformation.ConnectionPolicyVersion.Should().Be(ImapConnectionPolicyVersion.Legacy);
        serverInformation.MaxConcurrentClients.Should().Be(9);
        serverInformation.CalDavServiceUrl.Should().Be("https://dav.example.com");
    }

    [Fact]
    public async Task ExportAsync_MapsAccountPreferencesSignaturesAndFolders()
    {
        var accountId = Guid.NewGuid();

        await _accountService.CreateAccountAsync(
            new MailAccount
            {
                Id = accountId,
                Name = "Gmail",
                SenderName = "Gmail Sender",
                Address = "folders@example.com",
                ProviderType = MailProviderType.Gmail
            },
            null!);

        var preferences = (await _accountService.GetAccountAsync(accountId)).Preferences;
        preferences.IsNotificationsEnabled = true;
        preferences.IsTaskbarBadgeEnabled = false;
        preferences.IsDailyBriefingEnabled = true;
        preferences.IsSemanticIndexingEnabled = true;
        await _accountService.UpdateAccountPreferencesAsync(preferences);

        var signature = await _signatureService.CreateSignatureAsync(new AccountSignature
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            Name = "Work",
            HtmlBody = "<p>Regards</p>"
        });

        await _folderService.InsertFolderAsync(new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            RemoteFolderId = "INBOX",
            FolderName = "Inbox",
            SpecialFolderType = SpecialFolderType.Inbox,
            IsSticky = true,
            IsHidden = false,
            Order = 3,
            ShowUnreadCount = true
        });

        ReplaceUserMailboxesRequestDto? capturedRequest = null;
        _profileService
            .Setup(a => a.ReplaceMailboxesAsync(It.IsAny<ReplaceUserMailboxesRequestDto>(), It.IsAny<CancellationToken>()))
            .Callback<ReplaceUserMailboxesRequestDto, CancellationToken>((request, _) => capturedRequest = request)
            .Returns(Task.CompletedTask);

        var exportResult = await _service.ExportAsync(new WinoAccountSyncSelection(IncludePreferences: false, IncludeAccounts: true));

        exportResult.ExportedAccountDataCount.Should().Be(1);

        var exportedMailbox = capturedRequest!.Mailboxes.Single();
        exportedMailbox.IsNotificationsEnabled.Should().BeTrue();
        exportedMailbox.IsTaskbarBadgeEnabled.Should().BeFalse();
        exportedMailbox.IsMailAccessGranted.Should().NotBeNull();

        exportedMailbox.Signatures.Should().ContainSingle(a => a.Id == signature.Id && a.Name == "Work" && a.HtmlBody == "<p>Regards</p>");
        exportedMailbox.Folders.Should().ContainSingle(a => a.RemoteFolderId == "INBOX" && a.IsSticky && a.Order == 3 && a.ShowUnreadCount);
    }

    [Fact]
    public async Task ImportAsync_DuplicateMailbox_StillAppliesAccountDataAndFolderLayout()
    {
        var accountId = Guid.NewGuid();

        await _accountService.CreateAccountAsync(
            new MailAccount
            {
                Id = accountId,
                Name = "Existing Gmail",
                SenderName = "Existing Gmail",
                Address = "User@Example.com",
                ProviderType = MailProviderType.Gmail
            },
            null!);

        var untouchedSignature = await _signatureService.CreateSignatureAsync(new AccountSignature
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            Name = "Local only",
            HtmlBody = "<p>Local</p>"
        });

        await _folderService.InsertFolderAsync(new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = accountId,
            RemoteFolderId = "INBOX",
            FolderName = "Inbox",
            SpecialFolderType = SpecialFolderType.Inbox
        });

        var remoteSignatureId = Guid.NewGuid();

        _profileService
            .Setup(a => a.GetMailboxesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserMailboxSyncListDto(
            [
                new UserMailboxSyncItemDto
                {
                    Address = "user@example.com",
                    ProviderType = (int)MailProviderType.Gmail,
                    AccountName = "Duplicate Gmail",
                    IsNotificationsEnabled = false,
                    IsTaskbarBadgeEnabled = false,
                    SignatureIdForNewMessages = remoteSignatureId,
                    Signatures =
                    [
                        new UserMailboxSignatureSyncItemDto
                        {
                            Id = remoteSignatureId,
                            Name = "Work",
                            HtmlBody = "<p>Regards</p>"
                        }
                    ],
                    Folders =
                    [
                        new UserMailboxFolderSyncItemDto
                        {
                            RemoteFolderId = "INBOX",
                            IsSticky = true,
                            IsHidden = true,
                            Order = 7,
                            ShowUnreadCount = true
                        }
                    ]
                }
            ]));

        var result = await _service.ImportAsync(new WinoAccountSyncSelection(IncludePreferences: false, IncludeAccounts: true));

        result.ImportedMailboxCount.Should().Be(0);
        result.SkippedDuplicateMailboxCount.Should().Be(1);
        result.AppliedAccountDataCount.Should().Be(1);
        result.AppliedFolderConfigurationCount.Should().Be(1);
        result.HasAnyRemoteData.Should().BeTrue();

        var signatures = await _signatureService.GetSignaturesAsync(accountId);
        signatures.Should().Contain(a => a.Name == "Work" && a.HtmlBody == "<p>Regards</p>");
        signatures.Should().Contain(a => a.Id == untouchedSignature.Id);

        var localSignature = signatures.Single(a => a.Name == "Work");
        var preferences = (await _accountService.GetAccountAsync(accountId)).Preferences;
        preferences.SignatureIdForNewMessages.Should().Be(localSignature.Id);
        preferences.IsTaskbarBadgeEnabled.Should().BeFalse();

        var folder = await _folderService.GetFolderAsync(accountId, "INBOX");
        folder.IsSticky.Should().BeTrue();
        folder.IsHidden.Should().BeTrue();
        folder.Order.Should().Be(7);
        folder.ShowUnreadCount.Should().BeTrue();
    }

    [Fact]
    public async Task ImportAsync_NewMailbox_ParksFolderLayoutUntilTheFolderArrives()
    {
        _profileService
            .Setup(a => a.GetMailboxesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserMailboxSyncListDto(
            [
                new UserMailboxSyncItemDto
                {
                    Address = "fresh@example.com",
                    ProviderType = (int)MailProviderType.Gmail,
                    AccountName = "Fresh Gmail",
                    Folders =
                    [
                        new UserMailboxFolderSyncItemDto
                        {
                            RemoteFolderId = "Label_42",
                            IsSticky = true,
                            IsHidden = true,
                            Order = 5,
                            ShowUnreadCount = true,
                            IsJumpListEnabled = true
                        }
                    ]
                }
            ]));

        var result = await _service.ImportAsync(new WinoAccountSyncSelection(IncludePreferences: false, IncludeAccounts: true));

        result.ImportedMailboxCount.Should().Be(1);

        // The folder does not exist yet, so nothing could be applied directly.
        result.AppliedFolderConfigurationCount.Should().Be(0);

        var importedAccount = (await _accountService.GetAccountsAsync()).Single();
        var pendingOverrides = await _folderService.GetFolderConfigurationOverridesAsync(importedAccount.Id);
        pendingOverrides.Should().ContainSingle(a => a.RemoteFolderId == "Label_42" && a.Order == 5);

        // The synchronizer creates the folder with a brand new local id and the parked layout is applied.
        await _folderService.InsertFolderAsync(new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = importedAccount.Id,
            RemoteFolderId = "Label_42",
            FolderName = "Receipts",
            SpecialFolderType = SpecialFolderType.Other
        });

        var folder = await _folderService.GetFolderAsync(importedAccount.Id, "Label_42");
        folder.IsSticky.Should().BeTrue();
        folder.IsHidden.Should().BeTrue();
        folder.Order.Should().Be(5);
        folder.ShowUnreadCount.Should().BeTrue();
        folder.IsJumpListEnabled.Should().BeTrue();

        // The override must be consumed exactly once.
        (await _folderService.GetFolderConfigurationOverridesAsync(importedAccount.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_MailboxWithoutAccountData_LeavesLocalStateUntouched()
    {
        var accountId = Guid.NewGuid();

        await _accountService.CreateAccountAsync(
            new MailAccount
            {
                Id = accountId,
                Name = "Existing Gmail",
                SenderName = "Existing Gmail",
                Address = "user@example.com",
                ProviderType = MailProviderType.Gmail
            },
            null!);

        var preferences = (await _accountService.GetAccountAsync(accountId)).Preferences;
        preferences.IsNotificationsEnabled = true;
        await _accountService.UpdateAccountPreferencesAsync(preferences);

        // An older server does not send any of the new members.
        _profileService
            .Setup(a => a.GetMailboxesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserMailboxSyncListDto(
            [
                new UserMailboxSyncItemDto
                {
                    Address = "user@example.com",
                    ProviderType = (int)MailProviderType.Gmail,
                    AccountName = "Duplicate Gmail"
                }
            ]));

        var result = await _service.ImportAsync(new WinoAccountSyncSelection(IncludePreferences: false, IncludeAccounts: true));

        result.AppliedAccountDataCount.Should().Be(0);
        result.AppliedFolderConfigurationCount.Should().Be(0);

        var unchangedPreferences = (await _accountService.GetAccountAsync(accountId)).Preferences;
        unchangedPreferences.IsNotificationsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ImportFromJsonAsync_ReadsAccountDataFromVersionTwoFileAndAcceptsVersionOne()
    {
        var exportedAccountId = Guid.NewGuid();

        await _accountService.CreateAccountAsync(
            new MailAccount
            {
                Id = exportedAccountId,
                Name = "Gmail",
                SenderName = "Gmail",
                Address = "roundtrip@example.com",
                ProviderType = MailProviderType.Gmail
            },
            null!);

        await _folderService.InsertFolderAsync(new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = exportedAccountId,
            RemoteFolderId = "INBOX",
            FolderName = "Inbox",
            SpecialFolderType = SpecialFolderType.Inbox,
            IsHidden = true,
            Order = 4
        });

        var fileExport = await _service.ExportToJsonAsync(new WinoAccountSyncSelection(IncludePreferences: false, IncludeAccounts: true));
        fileExport.JsonContent.Should().Contain("\"RemoteFolderId\": \"INBOX\"");

        // Re-importing the same file resolves to the same account and reapplies the layout.
        var roundTripResult = await _service.ImportFromJsonAsync(fileExport.JsonContent);
        roundTripResult.SkippedDuplicateMailboxCount.Should().Be(1);
        roundTripResult.AppliedFolderConfigurationCount.Should().Be(1);

        // A version 1 file has no account data at all and must still import.
        const string legacyJson = """
        {
          "version": 1,
          "includesPreferences": false,
          "includesAccounts": true,
          "preferences": null,
          "mailboxes": [ { "Address": "legacy@example.com", "ProviderType": 1, "AccountName": "Legacy" } ]
        }
        """;

        var legacyResult = await _service.ImportFromJsonAsync(legacyJson);
        legacyResult.ImportedMailboxCount.Should().Be(1);
        legacyResult.AppliedAccountDataCount.Should().Be(0);
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
