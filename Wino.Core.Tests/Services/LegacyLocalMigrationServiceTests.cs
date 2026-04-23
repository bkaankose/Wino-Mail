using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using SQLite;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class LegacyLocalMigrationServiceTests
{
    [Fact]
    public async Task DetectAsync_ReturnsPreviewCountsAndDuplicatesByProvider()
    {
        await using var context = await LegacyMigrationTestContext.CreateAsync();

        await context.SeedCurrentAccountAsync(
            "gmail@example.com",
            MailProviderType.Gmail,
            "Existing Gmail");

        await context.InsertLegacyAccountAsync(
            Guid.NewGuid(),
            "outlook@example.com",
            MailProviderType.Outlook,
            order: 0,
            name: "Outlook Legacy");

        await context.InsertLegacyAccountAsync(
            Guid.NewGuid(),
            "gmail@example.com",
            MailProviderType.Gmail,
            order: 1,
            name: "Duplicate Gmail");

        var imapId = Guid.NewGuid();
        await context.InsertLegacyAccountAsync(
            imapId,
            "imap@example.com",
            MailProviderType.IMAP4,
            order: 2,
            name: "Imported IMAP",
            specialImapProvider: SpecialImapProvider.Yahoo);

        await context.InsertLegacyServerInformationAsync(
            imapId,
            address: "imap@example.com",
            incomingServer: "imap.mail.yahoo.com",
            incomingServerPort: "993",
            incomingServerUsername: "imap@example.com",
            outgoingServer: "smtp.mail.yahoo.com",
            outgoingServerPort: "587",
            outgoingServerUsername: "imap@example.com",
            calendarSupportMode: ImapCalendarSupportMode.CalDav,
            calDavServiceUrl: "https://caldav.calendar.yahoo.com/",
            calDavUsername: "imap@example.com");

        var preview = await context.Service.DetectAsync();

        preview.LegacyDatabaseExists.Should().BeTrue();
        preview.ShouldPrompt.Should().BeTrue();
        preview.LegacyAccountCount.Should().Be(3);
        preview.ImportableAccountCount.Should().Be(2);
        preview.DuplicateAccountCount.Should().Be(1);
        preview.Accounts.Select(a => a.Address).Should().ContainInOrder(
            "outlook@example.com",
            "gmail@example.com",
            "imap@example.com");

        preview.ProviderCounts.Should().ContainSingle(a =>
            a.ProviderType == MailProviderType.Outlook &&
            a.ImportableAccountCount == 1 &&
            a.DuplicateAccountCount == 0);

        preview.ProviderCounts.Should().ContainSingle(a =>
            a.ProviderType == MailProviderType.Gmail &&
            a.ImportableAccountCount == 0 &&
            a.DuplicateAccountCount == 1);

        preview.ProviderCounts.Should().ContainSingle(a =>
            a.ProviderType == MailProviderType.IMAP4 &&
            a.ImportableAccountCount == 1 &&
            a.DuplicateAccountCount == 0);
    }

    [Fact]
    public async Task ImportAsync_ImportsAccountsPreservesSafePreferencesAndRecreatesMergedInboxes()
    {
        await using var context = await LegacyMigrationTestContext.CreateAsync();

        var mergedInboxId = Guid.NewGuid();
        await context.InsertLegacyMergedInboxAsync(mergedInboxId, "Legacy Linked");

        var legacyOutlookId = Guid.NewGuid();
        var legacyGmailId = Guid.NewGuid();
        var legacyImapId = Guid.NewGuid();
        var legacySignatureId = Guid.NewGuid();

        await context.InsertLegacyAccountAsync(
            legacyOutlookId,
            "outlook@example.com",
            MailProviderType.Outlook,
            order: 0,
            name: "Outlook Legacy",
            senderName: "Outlook Sender",
            mergedInboxId: mergedInboxId);

        await context.InsertLegacyPreferencesAsync(
            legacyOutlookId,
            isNotificationsEnabled: false,
            isTaskbarBadgeEnabled: false,
            shouldAppendMessagesToSentFolder: true,
            isFocusedInboxEnabled: false,
            signatureIdForNewMessages: legacySignatureId,
            signatureIdForFollowingMessages: legacySignatureId);

        await context.InsertLegacyAccountAsync(
            legacyGmailId,
            "gmail@example.com",
            MailProviderType.Gmail,
            order: 1,
            name: "Gmail Legacy",
            senderName: "Gmail Sender",
            mergedInboxId: mergedInboxId);

        await context.InsertLegacyAccountAsync(
            legacyImapId,
            "imap@example.com",
            MailProviderType.IMAP4,
            order: 2,
            name: "iCloud Legacy",
            senderName: "iCloud Sender",
            specialImapProvider: SpecialImapProvider.iCloud);

        await context.InsertLegacyServerInformationAsync(
            legacyImapId,
            address: "imap@example.com",
            incomingServer: "imap.mail.me.com",
            incomingServerPort: "993",
            incomingServerUsername: "imap-user",
            outgoingServer: "smtp.mail.me.com",
            outgoingServerPort: "587",
            outgoingServerUsername: "smtp-user",
            calendarSupportMode: ImapCalendarSupportMode.CalDav,
            calDavServiceUrl: "https://caldav.icloud.com/",
            calDavUsername: "imap@example.com",
            maxConcurrentClients: 7);

        var result = await context.Service.ImportAsync();

        result.ImportedAccountCount.Should().Be(3);
        result.SkippedDuplicateAccountCount.Should().Be(0);
        result.FailedAccountCount.Should().Be(0);
        result.ImportedMergedInboxCount.Should().Be(1);
        result.SkippedMergedInboxCount.Should().Be(0);

        var accounts = await context.AccountService.GetAccountsAsync();
        accounts.Should().HaveCount(3);
        accounts.Select(a => a.Address).Should().ContainInOrder(
            "outlook@example.com",
            "gmail@example.com",
            "imap@example.com");

        var outlookAccount = accounts.Single(a => a.Address == "outlook@example.com");
        outlookAccount.AttentionReason.Should().Be(AccountAttentionReason.InvalidCredentials);
        outlookAccount.IsMailAccessGranted.Should().BeTrue();
        outlookAccount.IsCalendarAccessGranted.Should().BeTrue();
        outlookAccount.Preferences.IsNotificationsEnabled.Should().BeFalse();
        outlookAccount.Preferences.IsTaskbarBadgeEnabled.Should().BeFalse();
        outlookAccount.Preferences.ShouldAppendMessagesToSentFolder.Should().BeTrue();
        outlookAccount.Preferences.IsFocusedInboxEnabled.Should().BeFalse();
        outlookAccount.Preferences.SignatureIdForNewMessages.Should().NotBe(legacySignatureId);
        outlookAccount.Preferences.SignatureIdForFollowingMessages.Should().NotBe(legacySignatureId);

        var gmailAliases = await context.AccountService.GetAccountAliasesAsync(accounts.Single(a => a.Address == "gmail@example.com").Id);
        gmailAliases.Should().ContainSingle(a =>
            a.IsRootAlias &&
            a.IsPrimary &&
            a.AliasAddress == "gmail@example.com");

        var imapAccount = accounts.Single(a => a.Address == "imap@example.com");
        imapAccount.AttentionReason.Should().Be(AccountAttentionReason.InvalidCredentials);
        imapAccount.IsCalendarAccessGranted.Should().BeTrue();

        var serverInformation = await context.AccountService.GetAccountCustomServerInformationAsync(imapAccount.Id);
        serverInformation.Should().NotBeNull();
        serverInformation.IncomingServer.Should().Be("imap.mail.me.com");
        serverInformation.OutgoingServer.Should().Be("smtp.mail.me.com");
        serverInformation.IncomingServerPassword.Should().BeEmpty();
        serverInformation.OutgoingServerPassword.Should().BeEmpty();
        serverInformation.CalDavPassword.Should().BeEmpty();
        serverInformation.CalDavServiceUrl.Should().Be("https://caldav.icloud.com/");
        serverInformation.CalDavUsername.Should().Be("imap@example.com");
        serverInformation.MaxConcurrentClients.Should().Be(7);

        var mergedInboxIds = accounts
            .Where(a => a.Address is "outlook@example.com" or "gmail@example.com")
            .Select(a => a.MergedInboxId)
            .Distinct()
            .ToList();

        mergedInboxIds.Should().ContainSingle();
        mergedInboxIds[0].Should().NotBeNull();
        accounts.Single(a => a.Address == "outlook@example.com").MergedInbox.Name.Should().Be("Legacy Linked");
    }

    [Fact]
    public async Task ImportAsync_WithMissingLegacySchemaColumns_DefaultsSafelyAndSkipsIncompleteMergedInboxes()
    {
        await using var context = await LegacyMigrationTestContext.CreateAsync(new LegacySchemaOptions(
            IncludeCalDavColumns: false,
            IncludeCalendarSupportMode: false,
            IncludeTaskbarBadgeColumn: false,
            IncludeFocusedInboxColumn: false));

        await context.SeedCurrentAccountAsync(
            "duplicate@icloud.com",
            MailProviderType.IMAP4,
            "Existing iCloud",
            specialImapProvider: SpecialImapProvider.iCloud);

        var mergedInboxId = Guid.NewGuid();
        var duplicateLegacyAccountId = Guid.NewGuid();
        var importableLegacyAccountId = Guid.NewGuid();

        await context.InsertLegacyMergedInboxAsync(mergedInboxId, "Legacy Incomplete");

        await context.InsertLegacyAccountAsync(
            duplicateLegacyAccountId,
            "duplicate@icloud.com",
            MailProviderType.IMAP4,
            order: 0,
            name: "Duplicate iCloud",
            specialImapProvider: SpecialImapProvider.iCloud,
            mergedInboxId: mergedInboxId);

        await context.InsertLegacyAccountAsync(
            importableLegacyAccountId,
            "new@icloud.com",
            MailProviderType.IMAP4,
            order: 1,
            name: "Importable iCloud",
            specialImapProvider: SpecialImapProvider.iCloud,
            mergedInboxId: mergedInboxId);

        await context.InsertLegacyServerInformationAsync(
            duplicateLegacyAccountId,
            address: "duplicate@icloud.com",
            incomingServer: "imap.mail.me.com",
            incomingServerPort: "993",
            incomingServerUsername: "duplicate",
            outgoingServer: "smtp.mail.me.com",
            outgoingServerPort: "587",
            outgoingServerUsername: "duplicate");

        await context.InsertLegacyServerInformationAsync(
            importableLegacyAccountId,
            address: "new@icloud.com",
            incomingServer: "imap.mail.me.com",
            incomingServerPort: "993",
            incomingServerUsername: "new",
            outgoingServer: "smtp.mail.me.com",
            outgoingServerPort: "587",
            outgoingServerUsername: "new");

        var result = await context.Service.ImportAsync();

        result.ImportedAccountCount.Should().Be(1);
        result.SkippedDuplicateAccountCount.Should().Be(1);
        result.ImportedMergedInboxCount.Should().Be(0);
        result.SkippedMergedInboxCount.Should().Be(1);

        var importedAccount = (await context.AccountService.GetAccountsAsync())
            .Single(a => a.Address == "new@icloud.com");

        importedAccount.IsCalendarAccessGranted.Should().BeFalse();
        importedAccount.MergedInboxId.Should().BeNull();
        importedAccount.AttentionReason.Should().Be(AccountAttentionReason.InvalidCredentials);

        var importedServerInfo = await context.AccountService.GetAccountCustomServerInformationAsync(importedAccount.Id);
        importedServerInfo.Should().NotBeNull();
        importedServerInfo.CalDavServiceUrl.Should().Be("https://caldav.icloud.com/");
        importedServerInfo.CalDavUsername.Should().Be("new@icloud.com");
        importedServerInfo.CalDavPassword.Should().BeEmpty();
        importedServerInfo.CalendarSupportMode.Should().Be(ImapCalendarSupportMode.Disabled);
        importedServerInfo.IncomingServerPassword.Should().BeEmpty();
        importedServerInfo.OutgoingServerPassword.Should().BeEmpty();
    }

    private sealed class LegacyMigrationTestContext : IAsyncDisposable
    {
        private readonly SQLiteAsyncConnection _legacyConnection;
        private readonly InMemoryDatabaseService _databaseService;
        private readonly string _legacyFolderPath;

        public LegacyLocalMigrationService Service { get; }
        public AccountService AccountService { get; }

        private LegacyMigrationTestContext(string legacyFolderPath,
                                           SQLiteAsyncConnection legacyConnection,
                                           InMemoryDatabaseService databaseService,
                                           AccountService accountService,
                                           LegacyLocalMigrationService service)
        {
            _legacyFolderPath = legacyFolderPath;
            _legacyConnection = legacyConnection;
            _databaseService = databaseService;
            AccountService = accountService;
            Service = service;
        }

        public static async Task<LegacyMigrationTestContext> CreateAsync(LegacySchemaOptions? schemaOptions = null)
        {
            var databaseService = new InMemoryDatabaseService();
            await databaseService.InitializeAsync();

            var preferencesService = new Mock<IPreferencesService>();
            preferencesService.SetupProperty(a => a.StartupEntityId);

            var accountService = CreateAccountService(databaseService, preferencesService.Object);

            var legacyFolderPath = Path.Combine(Path.GetTempPath(), $"legacy-migration-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(legacyFolderPath);

            var legacyDatabasePath = Path.Combine(legacyFolderPath, "Wino180.db");
            var legacyConnection = new SQLiteAsyncConnection(legacyDatabasePath);

            await CreateLegacySchemaAsync(legacyConnection, schemaOptions ?? LegacySchemaOptions.Default);

            var applicationConfiguration = new ApplicationConfiguration
            {
                ApplicationDataFolderPath = legacyFolderPath,
                ApplicationTempFolderPath = legacyFolderPath,
                PublisherSharedFolderPath = legacyFolderPath
            };

            var service = new LegacyLocalMigrationService(
                applicationConfiguration,
                new InMemoryConfigurationService(),
                databaseService,
                accountService,
                new SpecialImapProviderConfigResolver());

            return new LegacyMigrationTestContext(
                legacyFolderPath,
                legacyConnection,
                databaseService,
                accountService,
                service);
        }

        public async Task SeedCurrentAccountAsync(string address,
                                                  MailProviderType providerType,
                                                  string name,
                                                  SpecialImapProvider specialImapProvider = SpecialImapProvider.None)
        {
            var accountId = Guid.NewGuid();
            CustomServerInformation? serverInformation = null;

            if (providerType == MailProviderType.IMAP4)
            {
                serverInformation = new CustomServerInformation
                {
                    Id = Guid.NewGuid(),
                    AccountId = accountId,
                    Address = address,
                    IncomingServer = "imap.current.test",
                    IncomingServerPort = "993",
                    IncomingServerUsername = address,
                    IncomingServerPassword = "secret",
                    IncomingServerSocketOption = ImapConnectionSecurity.Auto,
                    IncomingAuthenticationMethod = ImapAuthenticationMethod.NormalPassword,
                    OutgoingServer = "smtp.current.test",
                    OutgoingServerPort = "587",
                    OutgoingServerUsername = address,
                    OutgoingServerPassword = "secret",
                    OutgoingServerSocketOption = ImapConnectionSecurity.Auto,
                    OutgoingAuthenticationMethod = ImapAuthenticationMethod.NormalPassword,
                    CalDavServiceUrl = string.Empty,
                    CalDavUsername = string.Empty,
                    CalDavPassword = string.Empty,
                    CalendarSupportMode = ImapCalendarSupportMode.Disabled,
                    MaxConcurrentClients = 5
                };
            }

            await AccountService.CreateAccountAsync(
                new MailAccount
                {
                    Id = accountId,
                    Name = name,
                    SenderName = name,
                    Address = address,
                    ProviderType = providerType,
                    SpecialImapProvider = specialImapProvider,
                    IsMailAccessGranted = true,
                    IsCalendarAccessGranted = providerType is MailProviderType.Outlook or MailProviderType.Gmail
                },
                serverInformation);
        }

        public Task InsertLegacyAccountAsync(Guid accountId,
                                             string address,
                                             MailProviderType providerType,
                                             int order,
                                             string name,
                                             string? senderName = null,
                                             SpecialImapProvider specialImapProvider = SpecialImapProvider.None,
                                             Guid? mergedInboxId = null)
            => InsertRowAsync(
                _legacyConnection,
                "MailAccount",
                ("Id", accountId),
                ("Address", address),
                ("Name", name),
                ("SenderName", senderName ?? name),
                ("ProviderType", (int)providerType),
                ("SpecialImapProvider", (int)specialImapProvider),
                ("Order", order),
                ("AccountColorHex", "#123456"),
                ("MergedInboxId", mergedInboxId));

        public Task InsertLegacyPreferencesAsync(Guid accountId,
                                                 bool? isNotificationsEnabled = null,
                                                 bool? isTaskbarBadgeEnabled = null,
                                                 bool? shouldAppendMessagesToSentFolder = null,
                                                 bool? isFocusedInboxEnabled = null,
                                                 Guid? signatureIdForNewMessages = null,
                                                 Guid? signatureIdForFollowingMessages = null)
        {
            var values = new List<(string Column, object? Value)>
            {
                ("Id", Guid.NewGuid()),
                ("AccountId", accountId)
            };

            if (isNotificationsEnabled.HasValue)
                values.Add(("IsNotificationsEnabled", isNotificationsEnabled.Value));

            if (isTaskbarBadgeEnabled.HasValue)
                values.Add(("IsTaskbarBadgeEnabled", isTaskbarBadgeEnabled.Value));

            if (shouldAppendMessagesToSentFolder.HasValue)
                values.Add(("ShouldAppendMessagesToSentFolder", shouldAppendMessagesToSentFolder.Value));

            if (isFocusedInboxEnabled.HasValue)
                values.Add(("IsFocusedInboxEnabled", isFocusedInboxEnabled.Value));

            if (signatureIdForNewMessages.HasValue)
                values.Add(("SignatureIdForNewMessages", signatureIdForNewMessages.Value));

            if (signatureIdForFollowingMessages.HasValue)
                values.Add(("SignatureIdForFollowingMessages", signatureIdForFollowingMessages.Value));

            return InsertRowAsync(_legacyConnection, "MailAccountPreferences", values.ToArray());
        }

        public Task InsertLegacyServerInformationAsync(Guid accountId,
                                                       string address,
                                                       string incomingServer,
                                                       string incomingServerPort,
                                                       string incomingServerUsername,
                                                       string outgoingServer,
                                                       string outgoingServerPort,
                                                       string outgoingServerUsername,
                                                       ImapCalendarSupportMode? calendarSupportMode = null,
                                                       string? calDavServiceUrl = null,
                                                       string? calDavUsername = null,
                                                       int? maxConcurrentClients = null)
        {
            var values = new List<(string Column, object? Value)>
            {
                ("Id", Guid.NewGuid()),
                ("AccountId", accountId),
                ("Address", address),
                ("IncomingServer", incomingServer),
                ("IncomingServerPort", incomingServerPort),
                ("IncomingServerUsername", incomingServerUsername),
                ("IncomingServerSocketOption", (int)ImapConnectionSecurity.Auto),
                ("IncomingAuthenticationMethod", (int)ImapAuthenticationMethod.NormalPassword),
                ("OutgoingServer", outgoingServer),
                ("OutgoingServerPort", outgoingServerPort),
                ("OutgoingServerUsername", outgoingServerUsername),
                ("OutgoingServerSocketOption", (int)ImapConnectionSecurity.Auto),
                ("OutgoingAuthenticationMethod", (int)ImapAuthenticationMethod.NormalPassword),
                ("ProxyServer", "proxy.example.com"),
                ("ProxyServerPort", "8080"),
                ("MaxConcurrentClients", maxConcurrentClients ?? 5)
            };

            if (calendarSupportMode.HasValue)
                values.Add(("CalendarSupportMode", (int)calendarSupportMode.Value));

            if (!string.IsNullOrWhiteSpace(calDavServiceUrl))
                values.Add(("CalDavServiceUrl", calDavServiceUrl));

            if (!string.IsNullOrWhiteSpace(calDavUsername))
                values.Add(("CalDavUsername", calDavUsername));

            return InsertRowAsync(_legacyConnection, "CustomServerInformation", values.ToArray());
        }

        public Task InsertLegacyMergedInboxAsync(Guid mergedInboxId, string name)
            => InsertRowAsync(
                _legacyConnection,
                "MergedInbox",
                ("Id", mergedInboxId),
                ("Name", name));

        public async ValueTask DisposeAsync()
        {
            await _legacyConnection.CloseAsync();

            if (Directory.Exists(_legacyFolderPath))
            {
                Directory.Delete(_legacyFolderPath, recursive: true);
            }

            await _databaseService.DisposeAsync();
        }
    }

    private sealed class InMemoryConfigurationService : IConfigurationService
    {
        private readonly Dictionary<string, string?> _localValues = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _roamingValues = new(StringComparer.Ordinal);

        public bool Contains(string key) => _localValues.ContainsKey(key);

        public T Get<T>(string key, T defaultValue = default!)
            => TryGetValue(_localValues, key, defaultValue);

        public T GetRoaming<T>(string key, T defaultValue = default!)
            => TryGetValue(_roamingValues, key, defaultValue);

        public void Set(string key, object value)
            => _localValues[key] = value?.ToString();

        public void SetRoaming(string key, object value)
            => _roamingValues[key] = value?.ToString();

        private static T TryGetValue<T>(Dictionary<string, string?> values, string key, T defaultValue)
        {
            if (!values.TryGetValue(key, out var stringValue) || string.IsNullOrWhiteSpace(stringValue))
            {
                return defaultValue;
            }

            if (typeof(T).IsEnum)
            {
                return (T)Enum.Parse(typeof(T), stringValue);
            }

            if ((typeof(T) == typeof(Guid) || typeof(T) == typeof(Guid?)) && Guid.TryParse(stringValue, out var guid))
            {
                return (T)(object)guid;
            }

            return (T)Convert.ChangeType(stringValue, typeof(T));
        }
    }

    private sealed record LegacySchemaOptions(
        bool IncludeCalDavColumns,
        bool IncludeCalendarSupportMode,
        bool IncludeTaskbarBadgeColumn,
        bool IncludeFocusedInboxColumn)
    {
        public static LegacySchemaOptions Default => new(true, true, true, true);
    }

    private static async Task CreateLegacySchemaAsync(SQLiteAsyncConnection connection, LegacySchemaOptions options)
    {
        await connection.ExecuteAsync("""
CREATE TABLE MailAccount (
    Id TEXT PRIMARY KEY,
    Address TEXT NULL,
    Name TEXT NULL,
    SenderName TEXT NULL,
    ProviderType INTEGER NOT NULL,
    SpecialImapProvider INTEGER NOT NULL DEFAULT 0,
    [Order] INTEGER NOT NULL DEFAULT 0,
    AccountColorHex TEXT NULL,
    MergedInboxId TEXT NULL
)
""");

        var preferenceColumns = new List<string>
        {
            "Id TEXT PRIMARY KEY",
            "AccountId TEXT NOT NULL",
            "IsNotificationsEnabled INTEGER NULL",
            "ShouldAppendMessagesToSentFolder INTEGER NULL",
            "SignatureIdForNewMessages TEXT NULL",
            "SignatureIdForFollowingMessages TEXT NULL"
        };

        if (options.IncludeTaskbarBadgeColumn)
            preferenceColumns.Add("IsTaskbarBadgeEnabled INTEGER NULL");

        if (options.IncludeFocusedInboxColumn)
            preferenceColumns.Add("IsFocusedInboxEnabled INTEGER NULL");

        await connection.ExecuteAsync($"CREATE TABLE MailAccountPreferences ({string.Join(", ", preferenceColumns)})");

        var serverColumns = new List<string>
        {
            "Id TEXT PRIMARY KEY",
            "AccountId TEXT NOT NULL",
            "Address TEXT NULL",
            "IncomingServer TEXT NULL",
            "IncomingServerPort TEXT NULL",
            "IncomingServerUsername TEXT NULL",
            "IncomingServerSocketOption INTEGER NULL",
            "IncomingAuthenticationMethod INTEGER NULL",
            "OutgoingServer TEXT NULL",
            "OutgoingServerPort TEXT NULL",
            "OutgoingServerUsername TEXT NULL",
            "OutgoingServerSocketOption INTEGER NULL",
            "OutgoingAuthenticationMethod INTEGER NULL",
            "ProxyServer TEXT NULL",
            "ProxyServerPort TEXT NULL",
            "MaxConcurrentClients INTEGER NULL"
        };

        if (options.IncludeCalDavColumns)
        {
            serverColumns.Add("CalDavServiceUrl TEXT NULL");
            serverColumns.Add("CalDavUsername TEXT NULL");
        }

        if (options.IncludeCalendarSupportMode)
        {
            serverColumns.Add("CalendarSupportMode INTEGER NULL");
        }

        await connection.ExecuteAsync($"CREATE TABLE CustomServerInformation ({string.Join(", ", serverColumns)})");
        await connection.ExecuteAsync("""
CREATE TABLE MergedInbox (
    Id TEXT PRIMARY KEY,
    Name TEXT NULL
)
""");
    }

    private static Task InsertRowAsync(SQLiteAsyncConnection connection, string tableName, params (string Column, object? Value)[] values)
    {
        var columns = string.Join(", ", values.Select(a => $"[{a.Column}]"));
        var placeholders = string.Join(", ", values.Select(_ => "?"));
        var arguments = values.Select(a => ConvertLegacyValue(a.Value)).ToArray();

        return connection.ExecuteAsync($"INSERT INTO [{tableName}] ({columns}) VALUES ({placeholders})", arguments);
    }

    private static object? ConvertLegacyValue(object? value)
    {
        return value switch
        {
            null => null,
            bool boolValue => boolValue ? 1 : 0,
            Guid guidValue => guidValue.ToString(),
            _ => value
        };
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
