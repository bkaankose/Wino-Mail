#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Users;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.UI;

namespace Wino.Services;

public sealed class WinoAccountDataSyncService : IWinoAccountDataSyncService
{
    private const int DefaultMaxConcurrentClients = 5;

    /// <summary>
    /// Version 2 added per-account preferences, signatures and folder layout to the exported mailboxes.
    /// Version 1 files are still imported; they simply carry no account data.
    /// </summary>
    private const int LocalExportVersion = 2;

    private readonly IWinoAccountProfileService _profileService;
    private readonly IPreferencesService _preferencesService;
    private readonly IAccountService _accountService;
    private readonly IFolderService _folderService;
    private readonly ISignatureService _signatureService;

    public WinoAccountDataSyncService(
        IWinoAccountProfileService profileService,
        IPreferencesService preferencesService,
        IAccountService accountService,
        IFolderService folderService,
        ISignatureService signatureService)
    {
        _profileService = profileService;
        _preferencesService = preferencesService;
        _accountService = accountService;
        _folderService = folderService;
        _signatureService = signatureService;
    }

    public async Task<WinoAccountSyncExportResult> ExportAsync(WinoAccountSyncSelection selection, CancellationToken cancellationToken = default)
    {
        var preparedExport = await PrepareExportAsync(selection).ConfigureAwait(false);

        if (selection.IncludePreferences && preparedExport.PreferencesJson != null)
        {
            await _profileService.SaveSettingsAsync(preparedExport.PreferencesJson, cancellationToken).ConfigureAwait(false);
        }

        if (selection.IncludeAccounts)
        {
            var request = new ReplaceUserMailboxesRequestDto
            {
                Mailboxes = preparedExport.Mailboxes
            };

            await _profileService.ReplaceMailboxesAsync(request, cancellationToken).ConfigureAwait(false);
        }

        return preparedExport.ExportResult;
    }

    public async Task<WinoAccountSyncFileExportResult> ExportToJsonAsync(WinoAccountSyncSelection selection, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var preparedExport = await PrepareExportAsync(selection).ConfigureAwait(false);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", LocalExportVersion);
            writer.WriteString("exportedAtUtc", DateTime.UtcNow);
            writer.WriteBoolean("includesPreferences", preparedExport.ExportResult.IncludedPreferences);
            writer.WriteBoolean("includesAccounts", preparedExport.ExportResult.IncludedAccounts);

            writer.WritePropertyName("preferences");
            if (!string.IsNullOrWhiteSpace(preparedExport.PreferencesJson))
            {
                using var preferencesDocument = JsonDocument.Parse(preparedExport.PreferencesJson);
                preferencesDocument.RootElement.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WritePropertyName("mailboxes");
            JsonSerializer.Serialize(writer, preparedExport.Mailboxes, WinoAccountApiJsonContext.Default.ListUserMailboxSyncItemDto);
            writer.WriteEndObject();
        }

        return new WinoAccountSyncFileExportResult
        {
            JsonContent = Encoding.UTF8.GetString(stream.ToArray()),
            ExportResult = preparedExport.ExportResult
        };
    }

    public async Task<WinoAccountSyncImportResult> ImportAsync(WinoAccountSyncSelection selection, CancellationToken cancellationToken = default)
    {
        string? settingsJson = null;
        List<UserMailboxSyncItemDto> orderedMailboxes = [];

        if (selection.IncludePreferences)
        {
            settingsJson = await _profileService.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (selection.IncludeAccounts)
        {
            var mailboxes = await _profileService.GetMailboxesAsync(cancellationToken).ConfigureAwait(false);
            orderedMailboxes = mailboxes.Mailboxes
                .OrderBy(a => a.SortOrder)
                .ThenBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return await ImportDataAsync(selection, settingsJson, orderedMailboxes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WinoAccountSyncImportResult> ImportFromJsonAsync(string jsonContent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        jsonContent = TrimUtf8Bom(jsonContent);

        using var document = JsonDocument.Parse(jsonContent);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Invalid root element.");
        }

        string? settingsJson = null;
        if (document.RootElement.TryGetProperty("preferences", out var preferencesElement))
        {
            settingsJson = preferencesElement.ValueKind switch
            {
                JsonValueKind.Object => preferencesElement.GetRawText(),
                JsonValueKind.String => preferencesElement.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => throw new JsonException("Invalid preferences payload.")
            };
        }

        var mailboxes = new List<UserMailboxSyncItemDto>();
        if (document.RootElement.TryGetProperty("mailboxes", out var mailboxesElement))
        {
            if (mailboxesElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null or JsonValueKind.Undefined))
            {
                throw new JsonException("Invalid mailboxes payload.");
            }

            if (mailboxesElement.ValueKind == JsonValueKind.Array)
            {
                mailboxes = JsonSerializer.Deserialize(mailboxesElement.GetRawText(), WinoAccountApiJsonContext.Default.ListUserMailboxSyncItemDto) ?? [];
            }
        }

        var selection = new WinoAccountSyncSelection(
            IncludePreferences: !string.IsNullOrWhiteSpace(settingsJson),
            IncludeAccounts: mailboxes.Count > 0);

        return await ImportDataAsync(selection, settingsJson, mailboxes, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PreparedSyncExport> PrepareExportAsync(WinoAccountSyncSelection selection)
    {
        var preferencesJson = selection.IncludePreferences
            ? _preferencesService.ExportPreferences()
            : null;

        var mailboxes = new List<UserMailboxSyncItemDto>();
        var exportedAccountDataCount = 0;

        if (selection.IncludeAccounts)
        {
            var accounts = (await _accountService.GetAccountsAsync().ConfigureAwait(false)).OrderBy(a => a.Order);

            foreach (var account in accounts)
            {
                var mailbox = await MapMailboxAsync(account).ConfigureAwait(false);
                mailboxes.Add(mailbox);

                if (mailbox.Signatures?.Count > 0 || mailbox.Folders?.Count > 0)
                {
                    exportedAccountDataCount++;
                }
            }
        }

        return new PreparedSyncExport(
            preferencesJson,
            mailboxes,
            new WinoAccountSyncExportResult
            {
                IncludedPreferences = selection.IncludePreferences,
                IncludedAccounts = selection.IncludeAccounts,
                ExportedMailboxCount = mailboxes.Count,
                ExportedAccountDataCount = exportedAccountDataCount
            });
    }

    private async Task<WinoAccountSyncImportResult> ImportDataAsync(
        WinoAccountSyncSelection selection,
        string? settingsJson,
        List<UserMailboxSyncItemDto> mailboxes,
        CancellationToken cancellationToken)
    {
        var result = new WinoAccountSyncImportResult
        {
            IncludedPreferences = selection.IncludePreferences,
            IncludedAccounts = selection.IncludeAccounts
        };

        if (selection.IncludePreferences && !string.IsNullOrWhiteSpace(settingsJson))
        {
            var (appliedCount, failedCount) = _preferencesService.ImportPreferences(settingsJson);
            result = new WinoAccountSyncImportResult
            {
                IncludedPreferences = result.IncludedPreferences,
                IncludedAccounts = result.IncludedAccounts,
                HadRemotePreferences = true,
                AppliedPreferenceCount = appliedCount,
                FailedPreferenceCount = failedCount,
                ImportedMailboxCount = result.ImportedMailboxCount,
                SkippedDuplicateMailboxCount = result.SkippedDuplicateMailboxCount,
                RemoteMailboxCount = result.RemoteMailboxCount
            };
        }

        if (selection.IncludeAccounts)
        {
            var orderedMailboxes = mailboxes
                .OrderBy(a => a.SortOrder)
                .ThenBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var localAccounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            var existingKeys = localAccounts
                .Select(CreateMailboxKey)
                .ToHashSet(StringComparer.Ordinal);

            // Account data is applied to every mailbox that resolves to a local account, including the
            // ones skipped as duplicates. Two devices holding the same mailboxes is the common case, and
            // that is exactly when the settings are out of date.
            var accountsByKey = localAccounts.ToDictionary(CreateMailboxKey, StringComparer.Ordinal);

            var importedMailboxCount = 0;
            var skippedDuplicateMailboxCount = 0;

            foreach (var mailbox in orderedMailboxes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var mailboxKey = CreateMailboxKey(mailbox.Address, mailbox.ProviderType);
                if (!existingKeys.Add(mailboxKey))
                {
                    skippedDuplicateMailboxCount++;
                    continue;
                }

                var account = CreateImportedAccount(mailbox);
                var serverInformation = CreateImportedServerInformation(mailbox, account.Id);

                await _accountService.CreateAccountAsync(account, serverInformation).ConfigureAwait(false);

                if (account.IsMailAccessGranted)
                {
                    await _accountService.CreateRootAliasAsync(account.Id, account.Address).ConfigureAwait(false);
                }

                if (account.ProviderType == MailProviderType.IMAP4)
                {
                    var persistedAccount = await _accountService.GetAccountAsync(account.Id).ConfigureAwait(false);
                    if (persistedAccount != null && persistedAccount.AttentionReason != AccountAttentionReason.InvalidCredentials)
                    {
                        persistedAccount.AttentionReason = AccountAttentionReason.InvalidCredentials;
                        await _accountService.UpdateAccountAsync(persistedAccount).ConfigureAwait(false);
                    }
                }

                accountsByKey[mailboxKey] = account;
                importedMailboxCount++;
            }

            if (importedMailboxCount > 0)
            {
                WeakReferenceMessenger.Default.Send(new AccountsMenuRefreshRequested(false));
            }

            var appliedAccountDataCount = 0;
            var appliedFolderConfigurationCount = 0;

            foreach (var mailbox in orderedMailboxes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!accountsByKey.TryGetValue(CreateMailboxKey(mailbox.Address, mailbox.ProviderType), out var localAccount))
                {
                    continue;
                }

                var applied = await ApplyAccountDataAsync(localAccount, mailbox).ConfigureAwait(false);

                if (applied.AppliedAnything)
                {
                    appliedAccountDataCount++;
                }

                appliedFolderConfigurationCount += applied.AppliedFolderCount;
            }

            result = new WinoAccountSyncImportResult
            {
                IncludedPreferences = result.IncludedPreferences,
                IncludedAccounts = result.IncludedAccounts,
                HadRemotePreferences = result.HadRemotePreferences,
                AppliedPreferenceCount = result.AppliedPreferenceCount,
                FailedPreferenceCount = result.FailedPreferenceCount,
                ImportedMailboxCount = importedMailboxCount,
                SkippedDuplicateMailboxCount = skippedDuplicateMailboxCount,
                RemoteMailboxCount = orderedMailboxes.Count,
                AppliedAccountDataCount = appliedAccountDataCount,
                AppliedFolderConfigurationCount = appliedFolderConfigurationCount
            };
        }

        await RepairStartupEntityAsync().ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Applies the per-account preferences, signatures and folder layout carried by a synced mailbox.
    /// Older servers and version 1 export files carry none of this, in which case nothing happens.
    /// </summary>
    private async Task<AppliedAccountData> ApplyAccountDataAsync(MailAccount account, UserMailboxSyncItemDto mailbox)
    {
        var signatureIdMap = await ApplySignaturesAsync(account.Id, mailbox.Signatures).ConfigureAwait(false);
        var appliedPreferences = await ApplyAccountPreferencesAsync(account, mailbox, signatureIdMap).ConfigureAwait(false);
        var appliedFolderCount = await ApplyFolderConfigurationAsync(account.Id, mailbox.Folders).ConfigureAwait(false);

        var appliedAnything = appliedPreferences || signatureIdMap.Count > 0 || appliedFolderCount > 0;

        return new AppliedAccountData(appliedAnything, appliedFolderCount);
    }

    /// <summary>
    /// Matches incoming signatures to local ones by name and returns a source id to local id map so that
    /// the account preference pointers can be translated. Local signatures that are absent from the
    /// payload are never deleted.
    /// </summary>
    private async Task<Dictionary<Guid, Guid>> ApplySignaturesAsync(Guid accountId, List<UserMailboxSignatureSyncItemDto>? signatures)
    {
        var signatureIdMap = new Dictionary<Guid, Guid>();

        if (signatures == null || signatures.Count == 0) return signatureIdMap;

        var localSignatures = await _signatureService.GetSignaturesAsync(accountId).ConfigureAwait(false);

        foreach (var signature in signatures)
        {
            var name = signature.Name?.Trim();
            if (string.IsNullOrEmpty(name)) continue;

            var localSignature = localSignatures.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

            if (localSignature == null)
            {
                localSignature = await _signatureService.CreateSignatureAsync(new AccountSignature
                {
                    Id = Guid.NewGuid(),
                    MailAccountId = accountId,
                    Name = name,
                    HtmlBody = signature.HtmlBody ?? string.Empty
                }).ConfigureAwait(false);
            }
            else if (!string.Equals(localSignature.HtmlBody, signature.HtmlBody, StringComparison.Ordinal))
            {
                localSignature.HtmlBody = signature.HtmlBody ?? string.Empty;
                localSignature = await _signatureService.UpdateSignatureAsync(localSignature).ConfigureAwait(false);
            }

            if (localSignature != null)
            {
                signatureIdMap[signature.Id] = localSignature.Id;
            }
        }

        return signatureIdMap;
    }

    private async Task<bool> ApplyAccountPreferencesAsync(
        MailAccount account,
        UserMailboxSyncItemDto mailbox,
        Dictionary<Guid, Guid> signatureIdMap)
    {
        var persistedAccount = await _accountService.GetAccountAsync(account.Id).ConfigureAwait(false);
        var preferences = persistedAccount?.Preferences;

        if (preferences == null) return false;

        var hasChanges = false;

        hasChanges |= AssignIfProvided(mailbox.ShouldAppendMessagesToSentFolder, preferences.ShouldAppendMessagesToSentFolder,
            value => preferences.ShouldAppendMessagesToSentFolder = value);
        hasChanges |= AssignIfProvided(mailbox.IsNotificationsEnabled, preferences.IsNotificationsEnabled,
            value => preferences.IsNotificationsEnabled = value);
        hasChanges |= AssignIfProvided(mailbox.IsSignatureEnabled, preferences.IsSignatureEnabled,
            value => preferences.IsSignatureEnabled = value);
        hasChanges |= AssignIfProvided(mailbox.IsTaskbarBadgeEnabled, preferences.IsTaskbarBadgeEnabled,
            value => preferences.IsTaskbarBadgeEnabled = value);
        hasChanges |= AssignIfProvided(mailbox.IsJumpListEnabled, preferences.IsJumpListEnabled,
            value => preferences.IsJumpListEnabled = value);

        if (mailbox.IsFocusedInboxEnabled != preferences.IsFocusedInboxEnabled && mailbox.IsFocusedInboxEnabled.HasValue)
        {
            preferences.IsFocusedInboxEnabled = mailbox.IsFocusedInboxEnabled;
            hasChanges = true;
        }

        // Signature pointers reference ids from the exporting device. A pointer that cannot be
        // resolved against the freshly matched local signatures is dropped instead of dangling.
        hasChanges |= AssignSignaturePointer(mailbox.SignatureIdForNewMessages, signatureIdMap,
            preferences.SignatureIdForNewMessages, value => preferences.SignatureIdForNewMessages = value);
        hasChanges |= AssignSignaturePointer(mailbox.SignatureIdForFollowingMessages, signatureIdMap,
            preferences.SignatureIdForFollowingMessages, value => preferences.SignatureIdForFollowingMessages = value);

        if (!hasChanges) return false;

        await _accountService.UpdateAccountPreferencesAsync(preferences).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Applies the folder layout to folders that already exist, and parks the rest for the synchronizers.
    /// Returns how many entries were applied directly.
    /// </summary>
    private async Task<int> ApplyFolderConfigurationAsync(Guid accountId, List<UserMailboxFolderSyncItemDto>? folders)
    {
        if (folders == null || folders.Count == 0) return 0;

        var appliedFolderCount = 0;

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder.RemoteFolderId)) continue;

            var remoteFolderId = folder.RemoteFolderId.Trim();
            var localFolder = await _folderService.GetFolderAsync(accountId, remoteFolderId).ConfigureAwait(false);

            if (localFolder == null)
            {
                // The account was just imported and has no folders until it is re-authenticated
                // and synchronized. Park the layout until the folder arrives.
                await _folderService.UpsertFolderConfigurationOverrideAsync(new FolderConfigurationOverride
                {
                    MailAccountId = accountId,
                    RemoteFolderId = remoteFolderId,
                    IsSticky = folder.IsSticky,
                    IsHidden = folder.IsHidden,
                    Order = folder.Order,
                    ShowUnreadCount = folder.ShowUnreadCount,
                    IsJumpListEnabled = folder.IsJumpListEnabled
                }).ConfigureAwait(false);

                continue;
            }

            localFolder.IsSticky = folder.IsSticky;
            localFolder.IsHidden = folder.IsHidden;
            localFolder.Order = folder.Order;
            localFolder.ShowUnreadCount = folder.ShowUnreadCount;
            localFolder.IsJumpListEnabled = folder.IsJumpListEnabled;

            await _folderService.UpdateFolderAsync(localFolder).ConfigureAwait(false);

            appliedFolderCount++;
        }

        if (appliedFolderCount > 0)
        {
            // The bulk path has to announce the change itself. The single-folder mutators on
            // IFolderService do not all broadcast, so the shell would keep the stale layout.
            WeakReferenceMessenger.Default.Send(new AccountFolderConfigurationUpdated(accountId));
        }

        return appliedFolderCount;
    }

    private static bool AssignIfProvided(bool? incomingValue, bool currentValue, Action<bool> assign)
    {
        if (!incomingValue.HasValue || incomingValue.Value == currentValue) return false;

        assign(incomingValue.Value);

        return true;
    }

    private static bool AssignSignaturePointer(
        Guid? incomingSignatureId,
        Dictionary<Guid, Guid> signatureIdMap,
        Guid? currentSignatureId,
        Action<Guid?> assign)
    {
        if (!incomingSignatureId.HasValue) return false;

        if (!signatureIdMap.TryGetValue(incomingSignatureId.Value, out var localSignatureId)) return false;

        if (currentSignatureId == localSignatureId) return false;

        assign(localSignatureId);

        return true;
    }

    private async Task<UserMailboxSyncItemDto> MapMailboxAsync(MailAccount account)
    {
        var serverInformation = account.ProviderType == MailProviderType.IMAP4
            ? account.ServerInformation
            : null;

        var preferences = account.Preferences;
        var signatures = await _signatureService.GetSignaturesAsync(account.Id).ConfigureAwait(false);
        var folders = await _folderService.GetFoldersAsync(account.Id).ConfigureAwait(false);

        return new UserMailboxSyncItemDto
        {
            Address = account.Address ?? string.Empty,
            ProviderType = (int)account.ProviderType,
            SpecialImapProvider = (int)account.SpecialImapProvider,
            AccountName = account.Name,
            SenderName = account.SenderName,
            AccountColorHex = account.AccountColorHex,
            SortOrder = account.Order,
            IsCalendarAccessGranted = account.IsCalendarAccessGranted,
            CalendarSupportMode = serverInformation != null ? (int)serverInformation.CalendarSupportMode : 0,
            IncomingServer = serverInformation?.IncomingServer,
            IncomingServerPort = serverInformation?.IncomingServerPort,
            IncomingServerUsername = serverInformation?.IncomingServerUsername,
            IncomingServerSocketOption = serverInformation != null ? (int?)serverInformation.IncomingServerSocketOption : null,
            IncomingAuthenticationMethod = serverInformation != null ? (int?)serverInformation.IncomingAuthenticationMethod : null,
            OutgoingServer = serverInformation?.OutgoingServer,
            OutgoingServerPort = serverInformation?.OutgoingServerPort,
            OutgoingServerUsername = serverInformation?.OutgoingServerUsername,
            OutgoingServerSocketOption = serverInformation != null ? (int?)serverInformation.OutgoingServerSocketOption : null,
            OutgoingAuthenticationMethod = serverInformation != null ? (int?)serverInformation.OutgoingAuthenticationMethod : null,
            CalDavServiceUrl = serverInformation?.CalDavServiceUrl,
            CalDavUsername = serverInformation?.CalDavUsername,
            ProxyServer = serverInformation?.ProxyServer,
            ProxyServerPort = serverInformation?.ProxyServerPort,
            MaxConcurrentClients = serverInformation?.MaxConcurrentClients,
            IsMailAccessGranted = account.IsMailAccessGranted,

            // Intelligence preferences (daily briefing, semantic indexing) are intentionally not synced.
            // They must be enabled explicitly on every device.
            ShouldAppendMessagesToSentFolder = preferences?.ShouldAppendMessagesToSentFolder,
            IsNotificationsEnabled = preferences?.IsNotificationsEnabled,
            IsFocusedInboxEnabled = preferences?.IsFocusedInboxEnabled,
            IsSignatureEnabled = preferences?.IsSignatureEnabled,
            IsTaskbarBadgeEnabled = preferences?.IsTaskbarBadgeEnabled,
            IsJumpListEnabled = preferences?.IsJumpListEnabled,
            SignatureIdForNewMessages = preferences?.SignatureIdForNewMessages,
            SignatureIdForFollowingMessages = preferences?.SignatureIdForFollowingMessages,
            Signatures = signatures
                .Select(a => new UserMailboxSignatureSyncItemDto
                {
                    Id = a.Id,
                    Name = a.Name ?? string.Empty,
                    HtmlBody = a.HtmlBody ?? string.Empty
                })
                .ToList(),
            Folders = folders
                .Where(a => !string.IsNullOrEmpty(a.RemoteFolderId))
                .Select(a => new UserMailboxFolderSyncItemDto
                {
                    RemoteFolderId = a.RemoteFolderId,
                    IsSticky = a.IsSticky,
                    IsHidden = a.IsHidden,
                    Order = a.Order,
                    ShowUnreadCount = a.ShowUnreadCount,
                    IsJumpListEnabled = a.IsJumpListEnabled
                })
                .ToList()
        };
    }

    private static MailAccount CreateImportedAccount(UserMailboxSyncItemDto mailbox)
    {
        var providerType = (MailProviderType)mailbox.ProviderType;

        return new MailAccount
        {
            Id = Guid.NewGuid(),
            Address = mailbox.Address.Trim(),
            Name = string.IsNullOrWhiteSpace(mailbox.AccountName) ? mailbox.Address.Trim() : mailbox.AccountName.Trim(),
            SenderName = string.IsNullOrWhiteSpace(mailbox.SenderName) ? mailbox.Address.Trim() : mailbox.SenderName.Trim(),
            ProviderType = providerType,
            SpecialImapProvider = (SpecialImapProvider)mailbox.SpecialImapProvider,
            AccountColorHex = mailbox.AccountColorHex?.Trim() ?? string.Empty,
            Base64ProfilePictureData = string.Empty,
            CreatedAt = DateTime.UtcNow,
            InitialSynchronizationRange = InitialSynchronizationRange.SixMonths,
            IsMailAccessGranted = mailbox.IsMailAccessGranted ?? true,
            IsCalendarAccessGranted = mailbox.IsCalendarAccessGranted,
            SynchronizationDeltaIdentifier = string.Empty,
            CalendarSynchronizationDeltaIdentifier = string.Empty,
            AttentionReason = AccountAttentionReason.InvalidCredentials
        };
    }

    private static CustomServerInformation? CreateImportedServerInformation(UserMailboxSyncItemDto mailbox, Guid accountId)
    {
        var providerType = (MailProviderType)mailbox.ProviderType;
        if (providerType != MailProviderType.IMAP4)
        {
            return null;
        }

        return new CustomServerInformation
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Address = mailbox.Address.Trim(),
            IncomingServer = mailbox.IncomingServer?.Trim() ?? string.Empty,
            IncomingServerPort = mailbox.IncomingServerPort?.Trim() ?? string.Empty,
            IncomingServerUsername = mailbox.IncomingServerUsername?.Trim() ?? string.Empty,
            IncomingServerPassword = string.Empty,
            IncomingServerSocketOption = mailbox.IncomingServerSocketOption is int incomingSocketOption
                ? (ImapConnectionSecurity)incomingSocketOption
                : ImapConnectionSecurity.Auto,
            IncomingAuthenticationMethod = mailbox.IncomingAuthenticationMethod is int incomingAuthMethod
                ? (ImapAuthenticationMethod)incomingAuthMethod
                : ImapAuthenticationMethod.Auto,
            OutgoingServer = mailbox.OutgoingServer?.Trim() ?? string.Empty,
            OutgoingServerPort = mailbox.OutgoingServerPort?.Trim() ?? string.Empty,
            OutgoingServerUsername = mailbox.OutgoingServerUsername?.Trim() ?? string.Empty,
            OutgoingServerPassword = string.Empty,
            OutgoingServerSocketOption = mailbox.OutgoingServerSocketOption is int outgoingSocketOption
                ? (ImapConnectionSecurity)outgoingSocketOption
                : ImapConnectionSecurity.Auto,
            OutgoingAuthenticationMethod = mailbox.OutgoingAuthenticationMethod is int outgoingAuthMethod
                ? (ImapAuthenticationMethod)outgoingAuthMethod
                : ImapAuthenticationMethod.Auto,
            CalDavServiceUrl = mailbox.CalDavServiceUrl?.Trim() ?? string.Empty,
            CalDavUsername = mailbox.CalDavUsername?.Trim() ?? string.Empty,
            CalDavPassword = string.Empty,
            CalendarSupportMode = (ImapCalendarSupportMode)mailbox.CalendarSupportMode,
            ProxyServer = mailbox.ProxyServer?.Trim() ?? string.Empty,
            ProxyServerPort = mailbox.ProxyServerPort?.Trim() ?? string.Empty,
            MaxConcurrentClients = mailbox.MaxConcurrentClients.GetValueOrDefault(DefaultMaxConcurrentClients),
            ConnectionPolicyVersion = ImapConnectionPolicyVersion.Legacy
        };
    }

    private async Task RepairStartupEntityAsync()
    {
        if (!_preferencesService.StartupEntityId.HasValue)
        {
            return;
        }

        var startupEntityId = _preferencesService.StartupEntityId.Value;
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        var accountIds = accounts.Select(a => a.Id);
        var mergedInboxIds = accounts.Where(a => a.MergedInboxId.HasValue).Select(a => a.MergedInboxId!.Value);

        if (accountIds.Concat(mergedInboxIds).Contains(startupEntityId))
        {
            return;
        }

        _preferencesService.StartupEntityId = accounts.FirstOrDefault()?.Id;
    }

    private static string CreateMailboxKey(MailAccount account)
        => CreateMailboxKey(account.Address, (int)account.ProviderType);

    private static string CreateMailboxKey(string? address, int providerType)
        => $"{address?.Trim().ToLowerInvariant()}|{providerType}";

    private static string TrimUtf8Bom(string jsonContent)
        => !string.IsNullOrEmpty(jsonContent) && jsonContent[0] == '\uFEFF'
            ? jsonContent[1..]
            : jsonContent;

    private sealed record PreparedSyncExport(
        string? PreferencesJson,
        List<UserMailboxSyncItemDto> Mailboxes,
        WinoAccountSyncExportResult ExportResult);

    private readonly record struct AppliedAccountData(bool AppliedAnything, int AppliedFolderCount);
}
