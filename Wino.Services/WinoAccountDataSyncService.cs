#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Users;
using Wino.Messaging.Client.Accounts;

namespace Wino.Services;

public sealed class WinoAccountDataSyncService : IWinoAccountDataSyncService
{
    private const int DefaultMaxConcurrentClients = 5;
    private const int LocalExportVersion = 1;

    private readonly IWinoAccountProfileService _profileService;
    private readonly IPreferencesService _preferencesService;
    private readonly IAccountService _accountService;

    public WinoAccountDataSyncService(
        IWinoAccountProfileService profileService,
        IPreferencesService preferencesService,
        IAccountService accountService)
    {
        _profileService = profileService;
        _preferencesService = preferencesService;
        _accountService = accountService;
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

        var mailboxes = selection.IncludeAccounts
            ? (await _accountService.GetAccountsAsync().ConfigureAwait(false))
                .OrderBy(a => a.Order)
                .Select(MapMailbox)
                .ToList()
            : [];

        return new PreparedSyncExport(
            preferencesJson,
            mailboxes,
            new WinoAccountSyncExportResult
            {
                IncludedPreferences = selection.IncludePreferences,
                IncludedAccounts = selection.IncludeAccounts,
                ExportedMailboxCount = mailboxes.Count
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

                importedMailboxCount++;
            }

            if (importedMailboxCount > 0)
            {
                WeakReferenceMessenger.Default.Send(new AccountsMenuRefreshRequested(false));
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
                RemoteMailboxCount = orderedMailboxes.Count
            };
        }

        await RepairStartupEntityAsync().ConfigureAwait(false);

        return result;
    }

    private static UserMailboxSyncItemDto MapMailbox(MailAccount account)
    {
        var serverInformation = account.ProviderType == MailProviderType.IMAP4
            ? account.ServerInformation
            : null;

        var mailbox = new UserMailboxSyncItemDto
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
            MaxConcurrentClients = serverInformation?.MaxConcurrentClients
        };

        SetOptionalBooleanProperty(mailbox, "IsMailAccessGranted", account.IsMailAccessGranted);

        return mailbox;
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
            AccountColorHex = mailbox.AccountColorHex?.Trim(),
            Base64ProfilePictureData = string.Empty,
            CreatedAt = DateTime.UtcNow,
            InitialSynchronizationRange = InitialSynchronizationRange.SixMonths,
            IsMailAccessGranted = GetOptionalBooleanProperty(mailbox, "IsMailAccessGranted", defaultValue: true),
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
            IncomingServer = mailbox.IncomingServer?.Trim(),
            IncomingServerPort = mailbox.IncomingServerPort?.Trim(),
            IncomingServerUsername = mailbox.IncomingServerUsername?.Trim(),
            IncomingServerPassword = string.Empty,
            IncomingServerSocketOption = mailbox.IncomingServerSocketOption is int incomingSocketOption
                ? (ImapConnectionSecurity)incomingSocketOption
                : ImapConnectionSecurity.Auto,
            IncomingAuthenticationMethod = mailbox.IncomingAuthenticationMethod is int incomingAuthMethod
                ? (ImapAuthenticationMethod)incomingAuthMethod
                : ImapAuthenticationMethod.Auto,
            OutgoingServer = mailbox.OutgoingServer?.Trim(),
            OutgoingServerPort = mailbox.OutgoingServerPort?.Trim(),
            OutgoingServerUsername = mailbox.OutgoingServerUsername?.Trim(),
            OutgoingServerPassword = string.Empty,
            OutgoingServerSocketOption = mailbox.OutgoingServerSocketOption is int outgoingSocketOption
                ? (ImapConnectionSecurity)outgoingSocketOption
                : ImapConnectionSecurity.Auto,
            OutgoingAuthenticationMethod = mailbox.OutgoingAuthenticationMethod is int outgoingAuthMethod
                ? (ImapAuthenticationMethod)outgoingAuthMethod
                : ImapAuthenticationMethod.Auto,
            CalDavServiceUrl = mailbox.CalDavServiceUrl?.Trim(),
            CalDavUsername = mailbox.CalDavUsername?.Trim(),
            CalDavPassword = string.Empty,
            CalendarSupportMode = (ImapCalendarSupportMode)mailbox.CalendarSupportMode,
            ProxyServer = mailbox.ProxyServer?.Trim(),
            ProxyServerPort = mailbox.ProxyServerPort?.Trim(),
            MaxConcurrentClients = mailbox.MaxConcurrentClients.GetValueOrDefault(DefaultMaxConcurrentClients)
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

    private static bool GetOptionalBooleanProperty<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        T instance,
        string propertyName,
        bool defaultValue)
    {
        if (instance == null)
            return defaultValue;

        var property = typeof(T).GetProperty(propertyName);
        if (property?.PropertyType != typeof(bool) || !property.CanRead)
            return defaultValue;

        return property.GetValue(instance) is bool boolValue
            ? boolValue
            : defaultValue;
    }

    private static void SetOptionalBooleanProperty<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        T instance,
        string propertyName,
        bool value)
    {
        if (instance == null)
            return;

        var property = typeof(T).GetProperty(propertyName);
        if (property?.PropertyType == typeof(bool) && property.CanWrite)
        {
            property.SetValue(instance, value);
        }
    }

    private static string TrimUtf8Bom(string jsonContent)
        => !string.IsNullOrEmpty(jsonContent) && jsonContent[0] == '\uFEFF'
            ? jsonContent[1..]
            : jsonContent;

    private sealed record PreparedSyncExport(
        string? PreferencesJson,
        List<UserMailboxSyncItemDto> Mailboxes,
        WinoAccountSyncExportResult ExportResult);
}
