#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SQLite;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Messaging.Client.Accounts;

namespace Wino.Services;

public sealed class LegacyLocalMigrationService : ILegacyLocalMigrationService
{
    private const string LegacyDatabaseFileName = "Wino180.db";
    private const string MigrationCompletedSettingKey = "LegacyLocalMigration_v2_Completed";
    private const string PromptDeferredSettingKey = "LegacyLocalMigration_v2_PromptDeferred";
    private const int DefaultMaxConcurrentClients = 5;

    private readonly IApplicationConfiguration _applicationConfiguration;
    private readonly IConfigurationService _configurationService;
    private readonly IDatabaseService _databaseService;
    private readonly IAccountService _accountService;
    private readonly ISpecialImapProviderConfigResolver _specialImapProviderConfigResolver;
    private readonly ILogger _logger = Log.ForContext<LegacyLocalMigrationService>();

    public LegacyLocalMigrationService(IApplicationConfiguration applicationConfiguration,
                                       IConfigurationService configurationService,
                                       IDatabaseService databaseService,
                                       IAccountService accountService,
                                       ISpecialImapProviderConfigResolver specialImapProviderConfigResolver)
    {
        _applicationConfiguration = applicationConfiguration;
        _configurationService = configurationService;
        _databaseService = databaseService;
        _accountService = accountService;
        _specialImapProviderConfigResolver = specialImapProviderConfigResolver;
    }

    public void MarkPromptDeferred()
        => _configurationService.Set(PromptDeferredSettingKey, true);

    public async Task<LegacyLocalMigrationPreview> DetectAsync(CancellationToken cancellationToken = default)
    {
        var (_, preview) = await LoadPreviewContextAsync(cancellationToken).ConfigureAwait(false);
        return preview;
    }

    public async Task<LegacyLocalMigrationResult> ImportAsync(CancellationToken cancellationToken = default)
    {
        var (snapshot, preview) = await LoadPreviewContextAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot == null || !preview.LegacyDatabaseExists)
        {
            return new LegacyLocalMigrationResult
            {
                Preview = preview,
                Warnings = preview.Warnings
            };
        }

        _configurationService.Set(PromptDeferredSettingKey, false);

        var failures = new List<LegacyLocalMigrationFailure>();
        var importedAccounts = new Dictionary<Guid, MailAccount>();
        var skippedDuplicateAccountCount = preview.Accounts.Count(a => a.IsDuplicate);
        var importedAccountCount = 0;
        var currentAccounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        var nextOrder = currentAccounts.Count;

        foreach (var previewAccount in preview.Accounts
                     .OrderBy(a => a.Order)
                     .ThenBy(a => a.Address, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!previewAccount.CanImport ||
                !snapshot.AccountsById.TryGetValue(previewAccount.LegacyAccountId, out var legacyAccount))
            {
                continue;
            }

            try
            {
                var importedAccount = CreateImportedAccount(legacyAccount, nextOrder);
                var serverInformation = CreateImportedServerInformation(legacyAccount, importedAccount);

                await _accountService.CreateAccountAsync(importedAccount, serverInformation).ConfigureAwait(false);
                await _accountService.CreateRootAliasAsync(importedAccount.Id, importedAccount.Address).ConfigureAwait(false);

                ApplyLegacyPreferences(importedAccount, legacyAccount.Preferences);
                importedAccount.Order = nextOrder;

                await _accountService.UpdateAccountAsync(importedAccount).ConfigureAwait(false);

                importedAccounts[legacyAccount.LegacyAccountId] = importedAccount;
                importedAccountCount++;
                nextOrder++;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to import legacy account {LegacyAccountId} ({Address})", legacyAccount.LegacyAccountId, legacyAccount.Address);

                failures.Add(new LegacyLocalMigrationFailure
                {
                    Address = legacyAccount.Address,
                    ProviderType = legacyAccount.ProviderType,
                    Message = ex.Message
                });
            }
        }

        var (importedMergedInboxCount, skippedMergedInboxCount) = await ImportMergedInboxesAsync(snapshot, importedAccounts).ConfigureAwait(false);

        if (importedAccountCount > 0 || importedMergedInboxCount > 0)
        {
            WeakReferenceMessenger.Default.Send(new AccountsMenuRefreshRequested(false));
        }

        _configurationService.Set(MigrationCompletedSettingKey, failures.Count == 0);

        return new LegacyLocalMigrationResult
        {
            Preview = preview,
            ImportedAccountCount = importedAccountCount,
            SkippedDuplicateAccountCount = skippedDuplicateAccountCount,
            FailedAccountCount = failures.Count,
            ImportedMergedInboxCount = importedMergedInboxCount,
            SkippedMergedInboxCount = skippedMergedInboxCount,
            Failures = failures,
            Warnings = preview.Warnings
        };
    }

    private async Task<(LegacySnapshot? Snapshot, LegacyLocalMigrationPreview Preview)> LoadPreviewContextAsync(CancellationToken cancellationToken)
    {
        var legacyDatabasePath = GetLegacyDatabasePath();
        if (string.IsNullOrWhiteSpace(legacyDatabasePath) || !File.Exists(legacyDatabasePath))
        {
            return (null, CreateEmptyPreview(legacyDatabasePath));
        }

        SQLiteAsyncConnection? connection = null;

        try
        {
            connection = new SQLiteAsyncConnection(
                legacyDatabasePath,
                SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.SharedCache,
                storeDateTimeAsTicks: false);

            var snapshot = await LoadSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
            var existingAddressKeys = await TryGetExistingAddressKeysAsync().ConfigureAwait(false);

            return (snapshot, BuildPreview(legacyDatabasePath, snapshot, existingAddressKeys));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to inspect legacy database at {LegacyDatabasePath}", legacyDatabasePath);

            return (null, CreateUnreadablePreview(legacyDatabasePath));
        }
        finally
        {
            if (connection != null)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<LegacySnapshot> LoadSnapshotAsync(SQLiteAsyncConnection connection, CancellationToken cancellationToken)
    {
        var snapshot = new LegacySnapshot();

        var accountColumns = await GetColumnSetAsync(connection, nameof(MailAccount)).ConfigureAwait(false);
        if (accountColumns.Count == 0)
        {
            return snapshot;
        }

        var accountRows = await connection.QueryAsync<LegacyMailAccountRow>(BuildMailAccountQuery(accountColumns)).ConfigureAwait(false);
        snapshot.TotalLegacyAccountCount = accountRows.Count;

        var preferenceRows = await QueryRowsIfTableExistsAsync<LegacyMailAccountPreferencesRow>(
            connection,
            nameof(MailAccountPreferences),
            BuildMailAccountPreferencesQuery).ConfigureAwait(false);

        var serverRows = await QueryRowsIfTableExistsAsync<LegacyCustomServerInformationRow>(
            connection,
            nameof(CustomServerInformation),
            BuildCustomServerInformationQuery).ConfigureAwait(false);

        var mergedInboxRows = await QueryRowsIfTableExistsAsync<LegacyMergedInboxRow>(
            connection,
            nameof(MergedInbox),
            BuildMergedInboxQuery).ConfigureAwait(false);

        snapshot.MergedInboxNamesById = mergedInboxRows
            .Where(a => a.Id != Guid.Empty && !string.IsNullOrWhiteSpace(a.Name))
            .GroupBy(a => a.Id)
            .ToDictionary(a => a.Key, a => NormalizeOptionalText(a.First().Name), EqualityComparer<Guid>.Default);

        var preferencesByAccountId = preferenceRows
            .Where(a => a.AccountId != Guid.Empty)
            .GroupBy(a => a.AccountId)
            .ToDictionary(a => a.Key, a => a.First(), EqualityComparer<Guid>.Default);

        var serverByAccountId = serverRows
            .Where(a => a.AccountId != Guid.Empty)
            .GroupBy(a => a.AccountId)
            .ToDictionary(a => a.Key, a => a.First(), EqualityComparer<Guid>.Default);

        foreach (var row in accountRows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (row.Id == Guid.Empty ||
                !TryMapProviderType(row.ProviderType, out var providerType))
            {
                snapshot.InvalidAccountCount++;
                continue;
            }

            var normalizedAddress = NormalizeOptionalText(row.Address);
            if (string.IsNullOrWhiteSpace(normalizedAddress))
            {
                snapshot.InvalidAccountCount++;
                continue;
            }

            preferencesByAccountId.TryGetValue(row.Id, out var preferences);
            serverByAccountId.TryGetValue(row.Id, out var serverInformation);

            var candidate = new LegacyAccountCandidate
            {
                LegacyAccountId = row.Id,
                Address = normalizedAddress,
                Name = NormalizeDisplayName(row.Name, normalizedAddress),
                SenderName = NormalizeDisplayName(row.SenderName, NormalizeDisplayName(row.Name, normalizedAddress)),
                ProviderType = providerType,
                SpecialImapProvider = MapSpecialImapProvider(row.SpecialImapProvider),
                Order = Math.Max(0, row.Order ?? 0),
                AccountColorHex = NormalizeOptionalText(row.AccountColorHex),
                LegacyMergedInboxId = row.MergedInboxId,
                Preferences = preferences,
                ServerInformation = serverInformation
            };

            snapshot.Accounts.Add(candidate);
            snapshot.AccountsById[candidate.LegacyAccountId] = candidate;
        }

        snapshot.Accounts = snapshot.Accounts
            .OrderBy(a => a.Order)
            .ThenBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return snapshot;
    }

    private async Task<List<T>> QueryRowsIfTableExistsAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(
        SQLiteAsyncConnection connection,
        string tableName,
        Func<HashSet<string>, string> sqlFactory) where T : new()
    {
        var columns = await GetColumnSetAsync(connection, tableName).ConfigureAwait(false);
        if (columns.Count == 0)
        {
            return [];
        }

        return await connection.QueryAsync<T>(sqlFactory(columns)).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> GetColumnSetAsync(SQLiteAsyncConnection connection, string tableName)
    {
        var tableInfo = await connection.GetTableInfoAsync(tableName).ConfigureAwait(false);
        return tableInfo
            .Select(a => a.Name)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<HashSet<string>> TryGetExistingAddressKeysAsync()
    {
        if (_databaseService.Connection == null)
        {
            return [];
        }

        try
        {
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            return accounts
                .Select(a => CreateAddressKey(a.Address))
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Skipping duplicate detection against the current database because account data is not available yet.");
            return [];
        }
    }

    private LegacyLocalMigrationPreview BuildPreview(string legacyDatabasePath, LegacySnapshot snapshot, HashSet<string> existingAddressKeys)
    {
        var seenAddressKeys = new HashSet<string>(existingAddressKeys, StringComparer.Ordinal);
        var accountPreviewItems = new List<LegacyLocalMigrationAccountPreview>();

        foreach (var account in snapshot.Accounts)
        {
            var addressKey = CreateAddressKey(account.Address);
            var isDuplicate = !seenAddressKeys.Add(addressKey);
            var isCalendarEnabled = account.ProviderType switch
            {
                MailProviderType.Outlook or MailProviderType.Gmail => true,
                MailProviderType.IMAP4 => ResolveCalendarSupportMode(account) != ImapCalendarSupportMode.Disabled,
                _ => false
            };

            accountPreviewItems.Add(new LegacyLocalMigrationAccountPreview
            {
                LegacyAccountId = account.LegacyAccountId,
                Address = account.Address,
                DisplayName = account.Name,
                ProviderType = account.ProviderType,
                SpecialImapProvider = account.SpecialImapProvider,
                Order = account.Order,
                CanImport = !isDuplicate,
                IsDuplicate = isDuplicate,
                IsCalendarEnabled = isCalendarEnabled
            });
        }

        var importableMergedInboxCount = 0;
        var skippedMergedInboxCount = 0;

        foreach (var group in accountPreviewItems
                     .Where(a => snapshot.AccountsById[a.LegacyAccountId].LegacyMergedInboxId.HasValue)
                     .GroupBy(a => snapshot.AccountsById[a.LegacyAccountId].LegacyMergedInboxId!.Value))
        {
            var members = group.ToList();
            var hasReadableMergedInbox = snapshot.MergedInboxNamesById.TryGetValue(group.Key, out var mergedInboxName) &&
                                         !string.IsNullOrWhiteSpace(mergedInboxName);

            if (members.Count >= 2 && hasReadableMergedInbox && members.All(a => a.CanImport))
            {
                importableMergedInboxCount++;
            }
            else
            {
                skippedMergedInboxCount++;
            }
        }

        var providerCounts = accountPreviewItems
            .GroupBy(a => a.ProviderType)
            .OrderBy(a => a.Key)
            .Select(group => new LegacyLocalMigrationProviderCount
            {
                ProviderType = group.Key,
                TotalAccountCount = group.Count(),
                ImportableAccountCount = group.Count(a => a.CanImport),
                DuplicateAccountCount = group.Count(a => a.IsDuplicate)
            })
            .ToList();

        var hasCompletedMigration = _configurationService.Get(MigrationCompletedSettingKey, false);
        var isPromptDeferred = _configurationService.Get(PromptDeferredSettingKey, false);
        var importableAccountCount = accountPreviewItems.Count(a => a.CanImport);
        var warnings = BuildWarnings(snapshot, accountPreviewItems, skippedMergedInboxCount);

        return new LegacyLocalMigrationPreview
        {
            SourceDatabasePath = legacyDatabasePath,
            LegacyDatabaseExists = true,
            HasCompletedMigration = hasCompletedMigration,
            IsPromptDeferred = isPromptDeferred,
            ShouldPrompt = importableAccountCount > 0 && !hasCompletedMigration && !isPromptDeferred,
            LegacyAccountCount = snapshot.TotalLegacyAccountCount,
            ImportableAccountCount = importableAccountCount,
            DuplicateAccountCount = accountPreviewItems.Count(a => a.IsDuplicate),
            SkippedAccountCount = snapshot.InvalidAccountCount,
            ImportableMergedInboxCount = importableMergedInboxCount,
            SkippedMergedInboxCount = skippedMergedInboxCount,
            ProviderCounts = providerCounts,
            Accounts = accountPreviewItems,
            Warnings = warnings
        };
    }

    private static IReadOnlyList<string> BuildWarnings(LegacySnapshot snapshot,
                                                       IReadOnlyCollection<LegacyLocalMigrationAccountPreview> accountPreviewItems,
                                                       int skippedMergedInboxCount)
    {
        var warnings = new List<string>();

        if (accountPreviewItems.Any(a => a.CanImport && (a.ProviderType == MailProviderType.Outlook || a.ProviderType == MailProviderType.Gmail)))
        {
            warnings.Add(Translator.LegacyLocalMigration_Warning_OAuth);
        }

        if (accountPreviewItems.Any(a => a.CanImport && a.ProviderType == MailProviderType.IMAP4))
        {
            warnings.Add(Translator.LegacyLocalMigration_Warning_Imap);
        }

        if (accountPreviewItems.Any(a => snapshot.AccountsById[a.LegacyAccountId].LegacyMergedInboxId.HasValue))
        {
            warnings.Add(Translator.LegacyLocalMigration_Warning_Merged);
        }

        if (snapshot.InvalidAccountCount > 0)
        {
            warnings.Add(string.Format(Translator.LegacyLocalMigration_Warning_SkippedAccounts, snapshot.InvalidAccountCount));
        }

        if (skippedMergedInboxCount > 0)
        {
            warnings.Add(string.Format(Translator.LegacyLocalMigration_ImportMergedInboxesSkipped, skippedMergedInboxCount));
        }

        return warnings;
    }

    private MailAccount CreateImportedAccount(LegacyAccountCandidate account, int order)
    {
        var isCalendarAccessGranted = account.ProviderType switch
        {
            MailProviderType.Outlook or MailProviderType.Gmail => true,
            MailProviderType.IMAP4 => ResolveCalendarSupportMode(account) != ImapCalendarSupportMode.Disabled,
            _ => false
        };

        return new MailAccount
        {
            Id = Guid.NewGuid(),
            Address = account.Address,
            Name = NormalizeDisplayName(account.Name, account.Address),
            SenderName = NormalizeDisplayName(account.SenderName, NormalizeDisplayName(account.Name, account.Address)),
            ProviderType = account.ProviderType,
            SpecialImapProvider = account.SpecialImapProvider,
            SynchronizationDeltaIdentifier = string.Empty,
            CalendarSynchronizationDeltaIdentifier = string.Empty,
            AccountColorHex = account.AccountColorHex,
            Base64ProfilePictureData = string.Empty,
            Order = order,
            AttentionReason = AccountAttentionReason.InvalidCredentials,
            IsMailAccessGranted = true,
            IsCalendarAccessGranted = isCalendarAccessGranted,
            CreatedAt = DateTime.UtcNow,
            InitialSynchronizationRange = InitialSynchronizationRange.SixMonths
        };
    }

    private CustomServerInformation? CreateImportedServerInformation(LegacyAccountCandidate account, MailAccount importedAccount)
    {
        if (account.ProviderType != MailProviderType.IMAP4)
        {
            return null;
        }

        var legacyServer = account.ServerInformation;
        var fallbackServer = GetSpecialProviderFallback(account);

        return new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            AccountId = importedAccount.Id,
            Address = importedAccount.Address,
            IncomingServer = FirstNonEmpty(legacyServer?.IncomingServer, fallbackServer?.IncomingServer),
            IncomingServerUsername = FirstNonEmpty(legacyServer?.IncomingServerUsername, fallbackServer?.IncomingServerUsername),
            IncomingServerPassword = string.Empty,
            IncomingServerPort = FirstNonEmpty(legacyServer?.IncomingServerPort, fallbackServer?.IncomingServerPort),
            IncomingServerType = CustomIncomingServerType.IMAP4,
            OutgoingServer = FirstNonEmpty(legacyServer?.OutgoingServer, fallbackServer?.OutgoingServer),
            OutgoingServerPort = FirstNonEmpty(legacyServer?.OutgoingServerPort, fallbackServer?.OutgoingServerPort),
            OutgoingServerUsername = FirstNonEmpty(legacyServer?.OutgoingServerUsername, fallbackServer?.OutgoingServerUsername),
            OutgoingServerPassword = string.Empty,
            CalDavServiceUrl = FirstNonEmpty(legacyServer?.CalDavServiceUrl, fallbackServer?.CalDavServiceUrl),
            CalDavUsername = FirstNonEmpty(legacyServer?.CalDavUsername, fallbackServer?.CalDavUsername),
            CalDavPassword = string.Empty,
            CalendarSupportMode = ResolveCalendarSupportMode(account),
            IncomingServerSocketOption = MapConnectionSecurity(legacyServer?.IncomingServerSocketOption, fallbackServer?.IncomingServerSocketOption),
            IncomingAuthenticationMethod = MapAuthenticationMethod(legacyServer?.IncomingAuthenticationMethod, fallbackServer?.IncomingAuthenticationMethod),
            OutgoingServerSocketOption = MapConnectionSecurity(legacyServer?.OutgoingServerSocketOption, fallbackServer?.OutgoingServerSocketOption),
            OutgoingAuthenticationMethod = MapAuthenticationMethod(legacyServer?.OutgoingAuthenticationMethod, fallbackServer?.OutgoingAuthenticationMethod),
            ProxyServer = NormalizeOptionalText(legacyServer?.ProxyServer),
            ProxyServerPort = NormalizeOptionalText(legacyServer?.ProxyServerPort),
            MaxConcurrentClients = legacyServer?.MaxConcurrentClients is int maxConcurrentClients && maxConcurrentClients > 0
                ? maxConcurrentClients
                : fallbackServer?.MaxConcurrentClients > 0
                    ? fallbackServer.MaxConcurrentClients
                    : DefaultMaxConcurrentClients
        };
    }

    private static void ApplyLegacyPreferences(MailAccount account, LegacyMailAccountPreferencesRow? legacyPreferences)
    {
        if (account.Preferences == null || legacyPreferences == null)
        {
            return;
        }

        if (legacyPreferences.IsNotificationsEnabled.HasValue)
        {
            account.Preferences.IsNotificationsEnabled = legacyPreferences.IsNotificationsEnabled.Value;
        }

        if (legacyPreferences.IsTaskbarBadgeEnabled.HasValue)
        {
            account.Preferences.IsTaskbarBadgeEnabled = legacyPreferences.IsTaskbarBadgeEnabled.Value;
        }

        if (legacyPreferences.ShouldAppendMessagesToSentFolder.HasValue)
        {
            account.Preferences.ShouldAppendMessagesToSentFolder = legacyPreferences.ShouldAppendMessagesToSentFolder.Value;
        }

        if (account.ProviderType == MailProviderType.Outlook && legacyPreferences.IsFocusedInboxEnabled.HasValue)
        {
            account.Preferences.IsFocusedInboxEnabled = legacyPreferences.IsFocusedInboxEnabled.Value;
        }
    }

    private async Task<(int ImportedCount, int SkippedCount)> ImportMergedInboxesAsync(LegacySnapshot snapshot, IReadOnlyDictionary<Guid, MailAccount> importedAccounts)
    {
        var importedCount = 0;
        var skippedCount = 0;

        foreach (var group in snapshot.Accounts
                     .Where(a => a.LegacyMergedInboxId.HasValue)
                     .GroupBy(a => a.LegacyMergedInboxId!.Value))
        {
            var members = group.ToList();
            if (members.Count < 2 ||
                !snapshot.MergedInboxNamesById.TryGetValue(group.Key, out var mergedInboxName) ||
                string.IsNullOrWhiteSpace(mergedInboxName))
            {
                skippedCount++;
                continue;
            }

            var importedMembers = members
                .Where(a => importedAccounts.ContainsKey(a.LegacyAccountId))
                .Select(a => importedAccounts[a.LegacyAccountId])
                .ToList();

            if (importedMembers.Count != members.Count)
            {
                skippedCount++;
                continue;
            }

            try
            {
                await _accountService.CreateMergeAccountsAsync(
                    new MergedInbox { Name = mergedInboxName },
                    importedMembers).ConfigureAwait(false);

                importedCount++;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to import legacy merged inbox {LegacyMergedInboxId}", group.Key);
                skippedCount++;
            }
        }

        return (importedCount, skippedCount);
    }

    private CustomServerInformation? GetSpecialProviderFallback(LegacyAccountCandidate account)
    {
        if (account.SpecialImapProvider == SpecialImapProvider.None)
        {
            return null;
        }

        return _specialImapProviderConfigResolver.GetServerInformation(
            new MailAccount
            {
                Address = account.Address,
                SenderName = account.SenderName,
                ProviderType = MailProviderType.IMAP4,
                SpecialImapProvider = account.SpecialImapProvider
            },
            new AccountCreationDialogResult(
                MailProviderType.IMAP4,
                account.Name,
                new SpecialImapProviderDetails(
                    account.Address,
                    string.Empty,
                    account.SenderName,
                    account.SpecialImapProvider,
                    ImapCalendarSupportMode.CalDav),
                account.AccountColorHex,
                InitialSynchronizationRange.SixMonths,
                true,
                true));
    }

    private static ImapCalendarSupportMode ResolveCalendarSupportMode(LegacyAccountCandidate account)
    {
        var rawValue = account.ServerInformation?.CalendarSupportMode;

        return rawValue is int intValue && Enum.IsDefined(typeof(ImapCalendarSupportMode), intValue)
            ? (ImapCalendarSupportMode)intValue
            : ImapCalendarSupportMode.Disabled;
    }

    private static ImapConnectionSecurity MapConnectionSecurity(int? rawValue, ImapConnectionSecurity? fallbackValue)
    {
        if (rawValue.HasValue && Enum.IsDefined(typeof(ImapConnectionSecurity), rawValue.Value))
        {
            return (ImapConnectionSecurity)rawValue.Value;
        }

        return fallbackValue ?? ImapConnectionSecurity.Auto;
    }

    private static ImapAuthenticationMethod MapAuthenticationMethod(int? rawValue, ImapAuthenticationMethod? fallbackValue)
    {
        if (rawValue.HasValue && Enum.IsDefined(typeof(ImapAuthenticationMethod), rawValue.Value))
        {
            return (ImapAuthenticationMethod)rawValue.Value;
        }

        return fallbackValue ?? ImapAuthenticationMethod.Auto;
    }

    private static bool TryMapProviderType(int? rawValue, out MailProviderType providerType)
    {
        providerType = default;

        if (!rawValue.HasValue ||
            !Enum.IsDefined(typeof(MailProviderType), rawValue.Value))
        {
            return false;
        }

        providerType = (MailProviderType)rawValue.Value;

        return providerType is MailProviderType.Outlook or MailProviderType.Gmail or MailProviderType.IMAP4;
    }

    private static SpecialImapProvider MapSpecialImapProvider(int? rawValue)
    {
        if (!rawValue.HasValue ||
            !Enum.IsDefined(typeof(SpecialImapProvider), rawValue.Value))
        {
            return SpecialImapProvider.None;
        }

        return (SpecialImapProvider)rawValue.Value;
    }

    private static string BuildMailAccountQuery(HashSet<string> columns)
    {
        return $"""
SELECT
    {SelectColumnOrFallback(columns, "Id")},
    {SelectColumnOrFallback(columns, "Address")},
    {SelectColumnOrFallback(columns, "Name")},
    {SelectColumnOrFallback(columns, "SenderName")},
    {SelectColumnOrFallback(columns, "ProviderType")},
    {SelectColumnOrFallback(columns, "SpecialImapProvider")},
    {SelectColumnOrFallback(columns, "Order", "0")},
    {SelectColumnOrFallback(columns, "AccountColorHex")},
    {SelectColumnOrFallback(columns, "MergedInboxId")}
FROM [{nameof(MailAccount)}]
ORDER BY [Order] ASC, [Address] COLLATE NOCASE
""";
    }

    private static string BuildMailAccountPreferencesQuery(HashSet<string> columns)
    {
        return $"""
SELECT
    {SelectColumnOrFallback(columns, "AccountId")},
    {SelectColumnOrFallback(columns, "IsNotificationsEnabled")},
    {SelectColumnOrFallback(columns, "IsTaskbarBadgeEnabled")},
    {SelectColumnOrFallback(columns, "ShouldAppendMessagesToSentFolder")},
    {SelectColumnOrFallback(columns, "IsFocusedInboxEnabled")}
FROM [{nameof(MailAccountPreferences)}]
""";
    }

    private static string BuildCustomServerInformationQuery(HashSet<string> columns)
    {
        return $"""
SELECT
    {SelectColumnOrFallback(columns, "AccountId")},
    {SelectColumnOrFallback(columns, "Address")},
    {SelectColumnOrFallback(columns, "IncomingServer")},
    {SelectColumnOrFallback(columns, "IncomingServerPort")},
    {SelectColumnOrFallback(columns, "IncomingServerUsername")},
    {SelectColumnOrFallback(columns, "IncomingServerSocketOption")},
    {SelectColumnOrFallback(columns, "IncomingAuthenticationMethod")},
    {SelectColumnOrFallback(columns, "OutgoingServer")},
    {SelectColumnOrFallback(columns, "OutgoingServerPort")},
    {SelectColumnOrFallback(columns, "OutgoingServerUsername")},
    {SelectColumnOrFallback(columns, "OutgoingServerSocketOption")},
    {SelectColumnOrFallback(columns, "OutgoingAuthenticationMethod")},
    {SelectColumnOrFallback(columns, "CalDavServiceUrl")},
    {SelectColumnOrFallback(columns, "CalDavUsername")},
    {SelectColumnOrFallback(columns, "CalendarSupportMode")},
    {SelectColumnOrFallback(columns, "ProxyServer")},
    {SelectColumnOrFallback(columns, "ProxyServerPort")},
    {SelectColumnOrFallback(columns, "MaxConcurrentClients")}
FROM [{nameof(CustomServerInformation)}]
""";
    }

    private static string BuildMergedInboxQuery(HashSet<string> columns)
    {
        return $"""
SELECT
    {SelectColumnOrFallback(columns, "Id")},
    {SelectColumnOrFallback(columns, "Name")}
FROM [{nameof(MergedInbox)}]
""";
    }

    private static string SelectColumnOrFallback(HashSet<string> columns, string columnName, string fallbackSql = "NULL")
    {
        return columns.Contains(columnName)
            ? $"[{columnName}] AS [{columnName}]"
            : $"{fallbackSql} AS [{columnName}]";
    }

    private static string NormalizeDisplayName(string? value, string fallback)
    {
        var normalized = NormalizeOptionalText(value);
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string NormalizeOptionalText(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim();

    private static string FirstNonEmpty(string? primary, string? secondary)
    {
        var normalizedPrimary = NormalizeOptionalText(primary);
        if (!string.IsNullOrWhiteSpace(normalizedPrimary))
        {
            return normalizedPrimary;
        }

        return NormalizeOptionalText(secondary);
    }

    private static string CreateAddressKey(string? address)
        => NormalizeOptionalText(address).ToLowerInvariant();

    private string GetLegacyDatabasePath()
    {
        var publisherSharedFolderPath = _applicationConfiguration.PublisherSharedFolderPath;
        return string.IsNullOrWhiteSpace(publisherSharedFolderPath)
            ? string.Empty
            : Path.Combine(publisherSharedFolderPath, LegacyDatabaseFileName);
    }

    private LegacyLocalMigrationPreview CreateEmptyPreview(string legacyDatabasePath)
    {
        var hasCompletedMigration = _configurationService.Get(MigrationCompletedSettingKey, false);
        var isPromptDeferred = _configurationService.Get(PromptDeferredSettingKey, false);

        return new LegacyLocalMigrationPreview
        {
            SourceDatabasePath = legacyDatabasePath,
            LegacyDatabaseExists = false,
            HasCompletedMigration = hasCompletedMigration,
            IsPromptDeferred = isPromptDeferred,
            ShouldPrompt = false
        };
    }

    private LegacyLocalMigrationPreview CreateUnreadablePreview(string legacyDatabasePath)
    {
        var hasCompletedMigration = _configurationService.Get(MigrationCompletedSettingKey, false);
        var isPromptDeferred = _configurationService.Get(PromptDeferredSettingKey, false);

        return new LegacyLocalMigrationPreview
        {
            SourceDatabasePath = legacyDatabasePath,
            LegacyDatabaseExists = true,
            HasCompletedMigration = hasCompletedMigration,
            IsPromptDeferred = isPromptDeferred,
            ShouldPrompt = false,
            Warnings = [Translator.LegacyLocalMigration_Warning_ReadFailed]
        };
    }

    private sealed class LegacySnapshot
    {
        public int TotalLegacyAccountCount { get; set; }
        public int InvalidAccountCount { get; set; }
        public List<LegacyAccountCandidate> Accounts { get; set; } = [];
        public Dictionary<Guid, LegacyAccountCandidate> AccountsById { get; } = [];
        public Dictionary<Guid, string> MergedInboxNamesById { get; set; } = [];
    }

    private sealed class LegacyAccountCandidate
    {
        public Guid LegacyAccountId { get; init; }
        public string Address { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string SenderName { get; init; } = string.Empty;
        public MailProviderType ProviderType { get; init; }
        public SpecialImapProvider SpecialImapProvider { get; init; }
        public int Order { get; init; }
        public string AccountColorHex { get; init; } = string.Empty;
        public Guid? LegacyMergedInboxId { get; init; }
        public LegacyMailAccountPreferencesRow? Preferences { get; init; }
        public LegacyCustomServerInformationRow? ServerInformation { get; init; }
    }

    private sealed class LegacyMailAccountRow
    {
        public Guid Id { get; set; }
        public string? Address { get; set; }
        public string? Name { get; set; }
        public string? SenderName { get; set; }
        public int? ProviderType { get; set; }
        public int? SpecialImapProvider { get; set; }
        public int? Order { get; set; }
        public string? AccountColorHex { get; set; }
        public Guid? MergedInboxId { get; set; }
    }

    private sealed class LegacyMailAccountPreferencesRow
    {
        public Guid AccountId { get; set; }
        public bool? ShouldAppendMessagesToSentFolder { get; set; }
        public bool? IsNotificationsEnabled { get; set; }
        public bool? IsFocusedInboxEnabled { get; set; }
        public bool? IsTaskbarBadgeEnabled { get; set; }
    }

    private sealed class LegacyCustomServerInformationRow
    {
        public Guid AccountId { get; set; }
        public string? Address { get; set; }
        public string? IncomingServer { get; set; }
        public string? IncomingServerPort { get; set; }
        public string? IncomingServerUsername { get; set; }
        public int? IncomingServerSocketOption { get; set; }
        public int? IncomingAuthenticationMethod { get; set; }
        public string? OutgoingServer { get; set; }
        public string? OutgoingServerPort { get; set; }
        public string? OutgoingServerUsername { get; set; }
        public int? OutgoingServerSocketOption { get; set; }
        public int? OutgoingAuthenticationMethod { get; set; }
        public string? CalDavServiceUrl { get; set; }
        public string? CalDavUsername { get; set; }
        public int? CalendarSupportMode { get; set; }
        public string? ProxyServer { get; set; }
        public string? ProxyServerPort { get; set; }
        public int? MaxConcurrentClients { get; set; }
    }

    private sealed class LegacyMergedInboxRow
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
    }
}
