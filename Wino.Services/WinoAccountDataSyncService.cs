#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
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
        var exportedMailboxCount = 0;

        if (selection.IncludePreferences)
        {
            await _profileService.SaveSettingsAsync(_preferencesService.ExportPreferences(), cancellationToken).ConfigureAwait(false);
        }

        if (selection.IncludeAccounts)
        {
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
            var request = new ReplaceUserMailboxesRequestDto
            {
                Mailboxes = accounts
                    .OrderBy(a => a.Order)
                    .Select(MapMailbox)
                    .ToList()
            };

            await _profileService.ReplaceMailboxesAsync(request, cancellationToken).ConfigureAwait(false);
            exportedMailboxCount = request.Mailboxes.Count;
        }

        return new WinoAccountSyncExportResult
        {
            IncludedPreferences = selection.IncludePreferences,
            IncludedAccounts = selection.IncludeAccounts,
            ExportedMailboxCount = exportedMailboxCount
        };
    }

    public async Task<WinoAccountSyncImportResult> ImportAsync(WinoAccountSyncSelection selection, CancellationToken cancellationToken = default)
    {
        var result = new WinoAccountSyncImportResult
        {
            IncludedPreferences = selection.IncludePreferences,
            IncludedAccounts = selection.IncludeAccounts
        };

        if (selection.IncludePreferences)
        {
            var settingsJson = await _profileService.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(settingsJson))
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
        }

        if (selection.IncludeAccounts)
        {
            var mailboxes = await _profileService.GetMailboxesAsync(cancellationToken).ConfigureAwait(false);
            var orderedMailboxes = mailboxes.Mailboxes
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
                await _accountService.CreateRootAliasAsync(account.Id, account.Address).ConfigureAwait(false);

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
            MaxConcurrentClients = serverInformation?.MaxConcurrentClients
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
            AccountColorHex = mailbox.AccountColorHex?.Trim(),
            Base64ProfilePictureData = string.Empty,
            CreatedAt = DateTime.UtcNow,
            InitialSynchronizationRange = InitialSynchronizationRange.SixMonths,
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
}
