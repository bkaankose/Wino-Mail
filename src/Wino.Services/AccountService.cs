using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Misc;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.UI;

namespace Wino.Services;

public class AccountService : BaseDatabaseService, IAccountService
{
    public IAuthenticator ExternalAuthenticationAuthenticator { get; set; }

    private readonly ISignatureService _signatureService;
    private readonly IAuthenticationProvider _authenticationProvider;
    private readonly IMimeFileService _mimeFileService;
    private readonly IPreferencesService _preferencesService;
    private readonly IPictureStorageService _pictureStorageService;
    private readonly IServerCertificateTrustService _serverCertificateTrustService;
    private readonly ISemanticIndexJobRegistry _semanticIndexJobRegistry;
    private readonly IMailIntelligenceStore _localIntelligenceStore;
    private readonly ICardDavSynchronizationStore _cardDavSynchronizationStore;
    private readonly IDavCredentialStore _davCredentialStore;

    private readonly ILogger _logger = Log.ForContext<AccountService>();

    public AccountService(IDatabaseService databaseService,
                          ISignatureService signatureService,
                          IAuthenticationProvider authenticationProvider,
                          IMimeFileService mimeFileService,
                          IPreferencesService preferencesService,
                          IPictureStorageService pictureStorageService,
                          IServerCertificateTrustService serverCertificateTrustService = null,
                          ISemanticIndexJobRegistry semanticIndexJobRegistry = null,
                          IMailIntelligenceStore localIntelligenceStore = null,
                          ICardDavSynchronizationStore cardDavSynchronizationStore = null,
                          IDavCredentialStore davCredentialStore = null) : base(databaseService)
    {
        _signatureService = signatureService;
        _authenticationProvider = authenticationProvider;
        _mimeFileService = mimeFileService;
        _preferencesService = preferencesService;
        _pictureStorageService = pictureStorageService;
        _serverCertificateTrustService = serverCertificateTrustService ?? new ServerCertificateTrustService(databaseService);
        _semanticIndexJobRegistry = semanticIndexJobRegistry;
        _localIntelligenceStore = localIntelligenceStore;
        _cardDavSynchronizationStore = cardDavSynchronizationStore;
        _davCredentialStore = davCredentialStore;
    }


    public async Task ClearAccountAttentionAsync(Guid accountId)
    {
        var account = await GetAccountAsync(accountId);

        Guard.IsNotNull(account);

        account.AttentionReason = AccountAttentionReason.None;

        await UpdateAccountAsync(account);
    }

    public async Task UpdateMergedInboxAsync(Guid mergedInboxId, IEnumerable<Guid> linkedAccountIds)
    {
        // First, remove all accounts from merged inbox.
        await Connection.ExecuteAsync("UPDATE MailAccount SET MergedInboxId = NULL WHERE MergedInboxId = ?", mergedInboxId);

        // Then, add new accounts to merged inbox.
        var accountIdList = linkedAccountIds.ToList();
        var placeholders = string.Join(",", accountIdList.Select(_ => "?"));
        var sql = $"UPDATE MailAccount SET MergedInboxId = ? WHERE Id IN ({placeholders})";
        var parameters = new List<object> { mergedInboxId };
        parameters.AddRange(accountIdList.Cast<object>());

        await Connection.ExecuteAsync(sql, parameters.ToArray());

        WeakReferenceMessenger.Default.Send(new AccountsMenuRefreshRequested());
    }

    public async Task<string> UpdateSyncIdentifierRawAsync(Guid accountId, string syncIdentifier)
    {
        await Connection.ExecuteAsync("UPDATE MailAccount SET SynchronizationDeltaIdentifier = ? WHERE Id = ?", syncIdentifier, accountId);
        return syncIdentifier;
    }

    public async Task UnlinkMergedInboxAsync(Guid mergedInboxId)
    {
        var mergedInbox = await Connection.Table<MergedInbox>().FirstOrDefaultAsync(a => a.Id == mergedInboxId).ConfigureAwait(false);

        if (mergedInbox == null)
        {
            _logger.Warning("Could not find merged inbox with id {MergedInboxId}", mergedInboxId);

            return;
        }

        await Connection.ExecuteAsync("UPDATE MailAccount SET MergedInboxId = NULL WHERE MergedInboxId = ?", mergedInboxId).ConfigureAwait(false);
        await Connection.DeleteAsync<MergedInbox>(mergedInbox.Id).ConfigureAwait(false);

        // Change the startup entity id if it was the merged inbox.
        // Take the first account as startup account.

        if (_preferencesService.StartupEntityId == mergedInboxId)
        {
            var firstAccount = await Connection.Table<MailAccount>().FirstOrDefaultAsync();

            if (firstAccount != null)
            {
                _preferencesService.StartupEntityId = firstAccount.Id;
            }
            else
            {
                _preferencesService.StartupEntityId = null;
            }
        }

        WeakReferenceMessenger.Default.Send(new AccountsMenuRefreshRequested());
    }

    public async Task CreateMergeAccountsAsync(MergedInbox mergedInbox, IEnumerable<MailAccount> accountsToMerge)
    {
        if (mergedInbox == null) return;

        // 0. Give the merged inbox a new Guid.
        mergedInbox.Id = Guid.NewGuid();

        var accountFolderDictionary = new Dictionary<MailAccount, List<MailItemFolder>>();

        // 1. Make all folders in the accounts unsticky. We will stick them based on common special folder types.
        foreach (var account in accountsToMerge)
        {
            var accountFolderList = new List<MailItemFolder>();

            var folders = await Connection.Table<MailItemFolder>().Where(a => a.MailAccountId == account.Id).ToListAsync();

            foreach (var folder in folders)
            {
                accountFolderList.Add(folder);
                folder.IsSticky = false;

                await Connection.UpdateAsync(folder, typeof(MailItemFolder));
            }

            accountFolderDictionary.Add(account, accountFolderList);
        }

        // 2. Find the common special folders and stick them.
        // Only following types will be considered as common special folder.
        SpecialFolderType[] commonSpecialTypes =
        [
            SpecialFolderType.Inbox,
            SpecialFolderType.Sent,
            SpecialFolderType.Draft,
            SpecialFolderType.Archive,
            SpecialFolderType.Junk,
            SpecialFolderType.Deleted
        ];

        foreach (var type in commonSpecialTypes)
        {
            var isCommonType = accountFolderDictionary
                .Select(a => a.Value)
                .Where(a => a.Any(a => a.SpecialFolderType == type))
                .Count() == accountsToMerge.Count();

            if (isCommonType)
            {
                foreach (var account in accountsToMerge)
                {
                    var folder = accountFolderDictionary[account].FirstOrDefault(a => a.SpecialFolderType == type);

                    if (folder != null)
                    {
                        folder.IsSticky = true;

                        await Connection.UpdateAsync(folder, typeof(MailItemFolder));
                    }
                }
            }
        }

        // 3. Insert merged inbox and assign accounts.
        await Connection.InsertAsync(mergedInbox, typeof(MergedInbox));

        foreach (var account in accountsToMerge)
        {
            account.MergedInboxId = mergedInbox.Id;

            await Connection.UpdateAsync(account, typeof(MailAccount));
        }

        WeakReferenceMessenger.Default.Send(new AccountsMenuRefreshRequested());
    }

    public async Task RenameMergedAccountAsync(Guid mergedInboxId, string newName)
    {
        await Connection.ExecuteAsync("UPDATE MergedInbox SET Name = ? WHERE Id = ?", newName, mergedInboxId);

        ReportUIChange(new MergedInboxRenamed(mergedInboxId, newName));
    }

    public async Task FixTokenIssuesAsync(Guid accountId)
    {
        var account = await Connection.Table<MailAccount>().FirstOrDefaultAsync(a => a.Id == accountId);

        if (account == null) return;

        var authenticator = _authenticationProvider.GetAuthenticator(account.ProviderType);

        // This will re-generate token with interactive authentication
        // New authentication will include calendar scopes
        var token = await authenticator.GenerateTokenInformationAsync(account);

        Guard.IsNotNull(token);

        if (!string.IsNullOrWhiteSpace(token.AccountAddress))
            account.Address = token.AccountAddress;

        if (!string.IsNullOrWhiteSpace(token.AuthenticationAddress))
            account.AuthenticationAddress = token.AuthenticationAddress;

        await UpdateAccountAsync(account);
    }

    public Task<MailAccountPreferences> GetAccountPreferencesAsync(Guid accountId)
        => Connection.Table<MailAccountPreferences>().FirstOrDefaultAsync(a => a.AccountId == accountId);

    public async Task<List<MailAccount>> GetAccountsAsync()
    {
        var accounts = await Connection.Table<MailAccount>().OrderBy(a => a.Order).ToListAsync();

        foreach (var account in accounts)
        {
            // Load IMAP server configuration.
            if (account.ProviderType.IsCustomMailProvider())
                account.ServerInformation = await GetAccountCustomServerInformationAsync(account.Id);

            // Load MergedInbox information.
            if (account.MergedInboxId != null)
                account.MergedInbox = await GetMergedInboxInformationAsync(account.MergedInboxId.Value);

            account.Preferences = await GetAccountPreferencesAsync(account.Id);
        }

        return accounts;
    }

    public async Task<bool> AccountNameExistsAsync(string name, Guid? excludedAccountId = null)
    {
        var normalizedName = name?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedName))
            return false;

        var accounts = await Connection.Table<MailAccount>().ToListAsync().ConfigureAwait(false);

        return accounts.Any(account =>
            account.Id != excludedAccountId &&
            string.Equals(account.Name?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<bool> AccountAddressExistsAsync(string address, Guid? excludedAccountId = null)
    {
        var normalizedAddress = address?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedAddress))
            return false;

        var accounts = await Connection.Table<MailAccount>().ToListAsync().ConfigureAwait(false);

        return accounts.Any(account =>
            account.Id != excludedAccountId &&
            string.Equals(account.Address?.Trim(), normalizedAddress, StringComparison.OrdinalIgnoreCase));
    }

    public async Task CreateRootAliasAsync(Guid accountId, string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return;

        var rootAlias = new MailAccountAlias()
        {
            AccountId = accountId,
            AliasAddress = address,
            IsPrimary = true,
            IsRootAlias = true,
            IsVerified = true,
            ReplyToAddress = address,
            Id = Guid.NewGuid(),
            Source = AliasSource.Manual,
            SendCapability = AliasSendCapability.Confirmed
        };

        await Connection.InsertAsync(rootAlias, typeof(MailAccountAlias)).ConfigureAwait(false);

        Log.Information("Created root alias for the account {AccountId}", accountId);
    }

    public async Task<List<MailAccountAlias>> GetAccountAliasesAsync(Guid accountId)
    {
        return await Connection.QueryAsync<MailAccountAlias>(
            "SELECT * FROM MailAccountAlias WHERE AccountId = ? ORDER BY IsRootAlias DESC, IsPrimary DESC, AliasAddress ASC",
            accountId).ConfigureAwait(false);
    }

    private Task<MergedInbox> GetMergedInboxInformationAsync(Guid mergedInboxId)
        => Connection.Table<MergedInbox>().FirstOrDefaultAsync(a => a.Id == mergedInboxId);

    public async Task DeleteAccountMailCacheAsync(Guid accountId, AccountCacheResetReason accountCacheResetReason)
    {
        await Connection.ExecuteAsync(
            "DELETE FROM MailCopy WHERE Id IN (SELECT Id FROM MailCopy WHERE FolderId IN (SELECT Id FROM MailItemFolder WHERE MailAccountId = ?))",
            accountId);

        WeakReferenceMessenger.Default.Send(new AccountCacheResetMessage(accountId, accountCacheResetReason));
    }

    public async Task DeleteAccountMailDataAsync(Guid accountId)
    {
        await _mimeFileService.DeleteUserMimeCacheAsync(accountId).ConfigureAwait(false);

        await Connection.ExecuteAsync("UPDATE MailItemFolder SET DeltaToken = NULL WHERE MailAccountId = ?", accountId).ConfigureAwait(false);
        await Connection.ExecuteAsync("UPDATE MailAccount SET SynchronizationDeltaIdentifier = NULL WHERE Id = ?", accountId).ConfigureAwait(false);

        await DeleteAccountMailCacheAsync(accountId, AccountCacheResetReason.MailAccessDisabled).ConfigureAwait(false);
    }

    public async Task DeleteAccountAsync(MailAccount account)
    {
        if (_semanticIndexJobRegistry is not null)
            await _semanticIndexJobRegistry.CancelAndWaitAsync(account.Id).ConfigureAwait(false);

        await DeleteProviderTokenAsync(account).ConfigureAwait(false);
        if (_localIntelligenceStore is not null)
            await _localIntelligenceStore.DeleteAccountAsync(account.Id).ConfigureAwait(false);

        // Collect calendar entities before deletion so we can notify UI subscribers.
        var accountCalendars = await Connection.Table<AccountCalendar>()
            .Where(a => a.AccountId == account.Id)
            .ToListAsync()
            .ConfigureAwait(false);

        var deletedCalendarItems = new List<CalendarItem>();
        foreach (var accountCalendar in accountCalendars)
        {
            var calendarItems = await Connection.Table<CalendarItem>()
                .Where(a => a.CalendarId == accountCalendar.Id)
                .ToListAsync()
                .ConfigureAwait(false);

            deletedCalendarItems.AddRange(calendarItems);
        }

        await DeleteAccountMailCacheAsync(account.Id, AccountCacheResetReason.AccountRemoval);

        // Delete calendar metadata and related records for this account.
        foreach (var calendarItem in deletedCalendarItems)
        {
            await Connection.Table<CalendarEventAttendee>().DeleteAsync(a => a.CalendarItemId == calendarItem.Id).ConfigureAwait(false);
            await Connection.Table<Reminder>().DeleteAsync(a => a.CalendarItemId == calendarItem.Id).ConfigureAwait(false);
            await Connection.Table<CalendarAttachment>().DeleteAsync(a => a.CalendarItemId == calendarItem.Id).ConfigureAwait(false);
        }

        foreach (var accountCalendar in accountCalendars)
        {
            await Connection.Table<CalendarItem>().DeleteAsync(a => a.CalendarId == accountCalendar.Id).ConfigureAwait(false);
        }

        await Connection.Table<AccountCalendar>().DeleteAsync(a => a.AccountId == account.Id).ConfigureAwait(false);

        await Connection.Table<MailItemFolder>().DeleteAsync(a => a.MailAccountId == account.Id);
        await Connection.Table<FolderConfigurationOverride>().DeleteAsync(a => a.MailAccountId == account.Id);
        await Connection.Table<AccountSignature>().DeleteAsync(a => a.MailAccountId == account.Id);
        await Connection.Table<MailAccountAlias>().DeleteAsync(a => a.AccountId == account.Id);
        var filterIds = await Connection.Table<MailFilter>()
            .Where(a => a.MailAccountId == account.Id)
            .ToListAsync()
            .ConfigureAwait(false);
        foreach (var filter in filterIds)
        {
            await Connection.Table<MailFilterCondition>().DeleteAsync(a => a.MailFilterId == filter.Id).ConfigureAwait(false);
            await Connection.Table<MailFilterAction>().DeleteAsync(a => a.MailFilterId == filter.Id).ConfigureAwait(false);
            await Connection.Table<MailFilterExecution>().DeleteAsync(a => a.MailFilterId == filter.Id).ConfigureAwait(false);
        }
        await Connection.Table<MailFilter>().DeleteAsync(a => a.MailAccountId == account.Id).ConfigureAwait(false);
        await Connection.Table<AccountProviderFeature>().DeleteAsync(a => a.MailAccountId == account.Id).ConfigureAwait(false);

        var accountContacts = await Connection.Table<AccountContact>()
            .Where(contact => contact.MailAccountId == account.Id)
            .ToListAsync()
            .ConfigureAwait(false);

        if (_cardDavSynchronizationStore is not null)
            await _cardDavSynchronizationStore.DeleteAccountStateAsync(account.Id).ConfigureAwait(false);
        if (_davCredentialStore is not null)
            await _davCredentialStore.DeleteAsync(account.Id).ConfigureAwait(false);

        await Connection.RunInTransactionAsync(transaction =>
        {
            foreach (var contact in accountContacts)
            {
                transaction.Execute("DELETE FROM ContactEmailAddress WHERE ContactId = ?", contact.Id);
                transaction.Execute("DELETE FROM ContactPhoneNumber WHERE ContactId = ?", contact.Id);
                transaction.Execute("DELETE FROM ContactPostalAddress WHERE ContactId = ?", contact.Id);
                transaction.Execute("DELETE FROM ContactImAddress WHERE ContactId = ?", contact.Id);
                transaction.Execute("DELETE FROM ContactRelation WHERE ContactId = ?", contact.Id);
            }

            transaction.Execute("DELETE FROM ContactCard WHERE MailAccountId = ?", account.Id);
            transaction.Execute("DELETE FROM ContactAddressBook WHERE MailAccountId = ?", account.Id);
            transaction.Execute("DELETE FROM RecipientHistory WHERE AccountId = ?", account.Id);
            transaction.Execute("DELETE FROM TaskStep WHERE MailAccountId = ?", account.Id);
            transaction.Execute("DELETE FROM TaskCard WHERE MailAccountId = ?", account.Id);
            transaction.Execute("DELETE FROM TaskList WHERE MailAccountId = ?", account.Id);
        }).ConfigureAwait(false);

        // Account belongs to a merged inbox.
        // In case of there'll be a single account in the merged inbox, remove the merged inbox as well.

        if (account.MergedInboxId != null)
        {
            var mergedInboxAccountCount = await Connection.Table<MailAccount>().Where(a => a.MergedInboxId == account.MergedInboxId.Value).CountAsync();

            // There will be only one account in the merged inbox. Remove the link for the other account as well.
            if (mergedInboxAccountCount == 2)
            {
                await Connection.ExecuteAsync(
                    "UPDATE MailAccount SET MergedInboxId = NULL WHERE MergedInboxId = ?",
                    account.MergedInboxId.Value).ConfigureAwait(false);
            }
        }

        if (account.ProviderType.IsCustomMailProvider())
        {
            await Connection.Table<CustomServerInformation>().DeleteAsync(a => a.AccountId == account.Id);
            await Connection.Table<Pop3PendingServerDeletion>().DeleteAsync(a => a.AccountId == account.Id);
            await Connection.Table<Pop3RemoteMessageState>().DeleteAsync(a => a.AccountId == account.Id);
            await _serverCertificateTrustService.DeleteAccountTrustsAsync(account.Id).ConfigureAwait(false);
        }

        if (account.Preferences != null)
            await Connection.DeleteAsync<MailAccountPreferences>(account.Preferences.Id);

        await Connection.DeleteAsync<MailAccount>(account.Id);

        await _mimeFileService.DeleteUserMimeCacheAsync(account.Id).ConfigureAwait(false);

        if (account.ProfilePictureFileId is { } profilePictureFileId && _pictureStorageService != null)
            await _pictureStorageService.DeletePictureAsync(PictureKind.AccountProfile, profilePictureFileId).ConfigureAwait(false);

        if (_pictureStorageService is not null)
        {
            foreach (var contactPictureFileId in accountContacts
                         .Where(contact => contact.ContactPictureFileId.HasValue)
                         .Select(contact => contact.ContactPictureFileId.Value)
                         .Distinct())
            {
                await _pictureStorageService.DeletePictureAsync(PictureKind.Contact, contactPictureFileId).ConfigureAwait(false);
            }
        }

        // Clear out or set up a new startup entity id.
        // Next account after the deleted one will be the startup account.

        if (_preferencesService.StartupEntityId == account.Id || _preferencesService.StartupEntityId == account.MergedInboxId)
        {
            var firstNonStartupAccount = await Connection.Table<MailAccount>().FirstOrDefaultAsync(a => a.Id != account.Id);

            if (firstNonStartupAccount != null)
            {
                _preferencesService.StartupEntityId = firstNonStartupAccount.Id;
            }
            else
            {
                _preferencesService.StartupEntityId = null;
            }
        }

        foreach (var calendarItem in deletedCalendarItems)
        {
            WeakReferenceMessenger.Default.Send(new CalendarItemDeleted(calendarItem, EntityUpdateSource.Server));
        }

        foreach (var accountCalendar in accountCalendars)
        {
            WeakReferenceMessenger.Default.Send(new CalendarListDeleted(accountCalendar));
        }

        ReportUIChange(new AccountRemovedMessage(account));
    }

    public async Task DeleteAccountAuthenticationDataAsync(Guid accountId)
    {
        var account = await Connection.Table<MailAccount>()
            .FirstOrDefaultAsync(candidate => candidate.Id == accountId)
            .ConfigureAwait(false);

        Guard.IsNotNull(account);

        if (account.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook)
        {
            var authenticator = _authenticationProvider.GetAuthenticator(account.ProviderType);
            await authenticator.DeleteTokenInformationAsync(account).ConfigureAwait(false);
        }
        else if (account.ProviderType.IsCustomMailProvider())
        {
            var serverInformation = await GetAccountCustomServerInformationAsync(account.Id).ConfigureAwait(false);

            if (serverInformation is not null)
            {
                serverInformation.IncomingServerPassword = string.Empty;
                serverInformation.OutgoingServerPassword = string.Empty;
                serverInformation.CalDavPassword = string.Empty;
                await Connection.UpdateAsync(serverInformation, typeof(CustomServerInformation)).ConfigureAwait(false);
                account.ServerInformation = serverInformation;
            }
        }

        account.AttentionReason = AccountAttentionReason.InvalidCredentials;
        await UpdateAccountAsync(account).ConfigureAwait(false);
    }

    private async Task DeleteProviderTokenAsync(MailAccount account)
    {
        if (account == null || account.ProviderType is not (MailProviderType.Gmail or MailProviderType.Outlook))
            return;

        try
        {
            var authenticator = _authenticationProvider.GetAuthenticator(account.ProviderType);
            await authenticator.DeleteTokenInformationAsync(account).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete cached token for account {AccountId}. Continuing account deletion.", account.Id);
        }
    }

    public async Task UpdateProfileInformationAsync(
        Guid accountId,
        ProfileInformation profileInformation,
        bool removePictureWhenConfirmedAbsent = false)
    {
        var account = await GetAccountAsync(accountId).ConfigureAwait(false);
        Guid? profilePictureFileIdToDelete = null;
        Guid? newlyCreatedProfilePictureFileId = null;

        if (account != null)
        {
            if (!string.IsNullOrWhiteSpace(profileInformation.SenderName))
            {
                account.SenderName = profileInformation.SenderName;
            }

            if (profileInformation.ProfilePicture?.Status == ProfilePictureFetchStatus.Downloaded)
            {
                if (_pictureStorageService == null)
                    throw new InvalidOperationException("Account profile picture storage is unavailable.");

                var previousProfilePictureFileId = account.ProfilePictureFileId;
                var newProfilePictureFileId = await _pictureStorageService
                    .SavePictureAsync(PictureKind.AccountProfile, profileInformation.ProfilePicture.ImageData)
                    .ConfigureAwait(false);
                newlyCreatedProfilePictureFileId = newProfilePictureFileId;
                account.ProfilePictureFileId = newProfilePictureFileId;
                profilePictureFileIdToDelete = previousProfilePictureFileId;
                account.Base64ProfilePictureData = string.Empty;
                account.IsProfilePictureBackfillComplete = true;
            }
            else if (profileInformation.ProfilePicture?.Status == ProfilePictureFetchStatus.ConfirmedAbsent)
            {
                if (removePictureWhenConfirmedAbsent && account.ProfilePictureFileId is { } profilePictureFileId)
                {
                    if (_pictureStorageService == null)
                        throw new InvalidOperationException("Account profile picture storage is unavailable.");

                    profilePictureFileIdToDelete = profilePictureFileId;
                    account.ProfilePictureFileId = null;
                    account.Base64ProfilePictureData = string.Empty;
                }

                account.IsProfilePictureBackfillComplete = true;
            }

            var profileAddress = profileInformation.AccountAddress?.Trim();

            if (!string.IsNullOrWhiteSpace(profileAddress) &&
                !string.Equals(account.Address?.Trim(), profileAddress, StringComparison.OrdinalIgnoreCase))
            {
                if (await AccountAddressExistsAsync(profileAddress, account.Id).ConfigureAwait(false))
                    throw new InvalidOperationException(Translator.DialogMessage_AccountAddressExistsMessage);

                account.Address = profileAddress;
            }

            try
            {
                await UpdateAccountAsync(account).ConfigureAwait(false);
            }
            catch
            {
                if (newlyCreatedProfilePictureFileId is { } failedProfilePictureFileId)
                {
                    await _pictureStorageService
                        .DeletePictureAsync(PictureKind.AccountProfile, failedProfilePictureFileId)
                        .ConfigureAwait(false);
                }

                throw;
            }

            if (profilePictureFileIdToDelete is { } obsoleteProfilePictureFileId)
            {
                await _pictureStorageService
                    .DeletePictureAsync(PictureKind.AccountProfile, obsoleteProfilePictureFileId)
                    .ConfigureAwait(false);
            }
        }
    }

    public async Task<MailAccount> GetAccountAsync(Guid accountId)
    {
        var account = await Connection.Table<MailAccount>().FirstOrDefaultAsync(a => a.Id == accountId);

        if (account == null)
        {
            _logger.Error("Could not find account with id {AccountId}", accountId);
        }
        else
        {
            if (account.ProviderType.IsCustomMailProvider())
                account.ServerInformation = await GetAccountCustomServerInformationAsync(account.Id);

            account.Preferences = await GetAccountPreferencesAsync(account.Id);

            return account;
        }

        return null;
    }

    public Task<CustomServerInformation> GetAccountCustomServerInformationAsync(Guid accountId)
        => Connection.Table<CustomServerInformation>().FirstOrDefaultAsync(a => a.AccountId == accountId);

    public async Task UpdateAccountAsync(MailAccount account)
    {
        if (account.Preferences is not null)
        {
            account.Preferences.PrepareForStorage();
            await Connection.UpdateAsync(account.Preferences, typeof(MailAccountPreferences)).ConfigureAwait(false);
        }

        await Connection.UpdateAsync(account, typeof(MailAccount)).ConfigureAwait(false);

        ReportUIChange(new AccountUpdatedMessage(account));

        if (account.Preferences is not null)
            WeakReferenceMessenger.Default.Send(new IntelligenceVisibilityChanged(
                account.Id,
                [.. account.Preferences.ExcludedIntelligenceIndicatorIds]));
    }

    public async Task UpdateAccountPreferencesAsync(MailAccountPreferences preferences)
    {
        Guard.IsNotNull(preferences);

        preferences.PrepareForStorage();
        await Connection.UpdateAsync(preferences, typeof(MailAccountPreferences)).ConfigureAwait(false);
        WeakReferenceMessenger.Default.Send(new IntelligenceVisibilityChanged(
            preferences.AccountId,
            [.. preferences.ExcludedIntelligenceIndicatorIds]));
    }

    public async Task UpdateAccountCustomServerInformationAsync(CustomServerInformation customServerInformation)
    {
        var previous = await GetAccountCustomServerInformationAsync(customServerInformation.AccountId).ConfigureAwait(false);
        await Connection.InsertOrReplaceAsync(customServerInformation, typeof(CustomServerInformation)).ConfigureAwait(false);
        await UpdateCertificateTrustsAsync(previous, customServerInformation).ConfigureAwait(false);
    }

    public async Task UpdateImapConnectionSettingsAsync(MailAccount account, CustomServerInformation customServerInformation)
    {
        Guard.IsNotNull(account);
        Guard.IsNotNull(customServerInformation);

        var previous = await GetAccountCustomServerInformationAsync(account.Id).ConfigureAwait(false);
        customServerInformation.AccountId = account.Id;
        account.Preferences?.PrepareForStorage();

        await Connection.RunInTransactionAsync(connection =>
        {
            connection.InsertOrReplace(customServerInformation, typeof(CustomServerInformation));
            if (account.Preferences != null)
                connection.Update(account.Preferences, typeof(MailAccountPreferences));
            connection.Update(account, typeof(MailAccount));

            DeleteChangedEndpointTrust(connection, account.Id, MailServerProtocol.Imap,
                previous?.IncomingServer, previous?.IncomingServerPort,
                customServerInformation.IncomingServer, customServerInformation.IncomingServerPort);
            DeleteChangedEndpointTrust(connection, account.Id, MailServerProtocol.Smtp,
                previous?.OutgoingServer, previous?.OutgoingServerPort,
                customServerInformation.OutgoingServer, customServerInformation.OutgoingServerPort);

            foreach (var trust in customServerInformation.PendingCertificateTrusts ?? [])
            {
                trust.AccountId = account.Id;
                trust.Host = NormalizeHost(trust.Host);
                trust.Id = trust.Id == Guid.Empty ? Guid.NewGuid() : trust.Id;
                connection.Execute(
                    $"DELETE FROM {nameof(MailServerCertificateTrust)} WHERE {nameof(MailServerCertificateTrust.AccountId)} = ? AND {nameof(MailServerCertificateTrust.Protocol)} = ? AND {nameof(MailServerCertificateTrust.Host)} = ? AND {nameof(MailServerCertificateTrust.Port)} = ?",
                    account.Id, (int)trust.Protocol, trust.Host, trust.Port);
                connection.Insert(trust, typeof(MailServerCertificateTrust));
            }
        }).ConfigureAwait(false);

        customServerInformation.PendingCertificateTrusts.Clear();
        account.ServerInformation = customServerInformation;
        ReportUIChange(new AccountUpdatedMessage(account));
    }

    private async Task UpdateCertificateTrustsAsync(CustomServerInformation previous, CustomServerInformation current)
    {
        if (previous != null)
        {
            if (!EndpointEquals(previous.IncomingServer, previous.IncomingServerPort, current.IncomingServer, current.IncomingServerPort))
                await _serverCertificateTrustService.DeleteEndpointTrustAsync(current.AccountId, MailServerProtocol.Imap, previous.IncomingServer, ParsePort(previous.IncomingServerPort)).ConfigureAwait(false);

            if (!EndpointEquals(previous.OutgoingServer, previous.OutgoingServerPort, current.OutgoingServer, current.OutgoingServerPort))
                await _serverCertificateTrustService.DeleteEndpointTrustAsync(current.AccountId, MailServerProtocol.Smtp, previous.OutgoingServer, ParsePort(previous.OutgoingServerPort)).ConfigureAwait(false);
        }

        await _serverCertificateTrustService.SaveTrustsAsync(current.AccountId, current.PendingCertificateTrusts).ConfigureAwait(false);
        current.PendingCertificateTrusts.Clear();
    }

    private static void DeleteChangedEndpointTrust(SQLite.SQLiteConnection connection, Guid accountId, MailServerProtocol protocol,
        string oldHost, string oldPort, string newHost, string newPort)
    {
        if (EndpointEquals(oldHost, oldPort, newHost, newPort))
            return;

        connection.Execute(
            $"DELETE FROM {nameof(MailServerCertificateTrust)} WHERE {nameof(MailServerCertificateTrust.AccountId)} = ? AND {nameof(MailServerCertificateTrust.Protocol)} = ? AND {nameof(MailServerCertificateTrust.Host)} = ? AND {nameof(MailServerCertificateTrust.Port)} = ?",
            accountId, (int)protocol, NormalizeHost(oldHost), ParsePort(oldPort));
    }

    private static bool EndpointEquals(string firstHost, string firstPort, string secondHost, string secondPort)
        => string.Equals(NormalizeHost(firstHost), NormalizeHost(secondHost), StringComparison.Ordinal) &&
           ParsePort(firstPort) == ParsePort(secondPort);

    private static string NormalizeHost(string host) => host?.Trim().ToLowerInvariant() ?? string.Empty;
    private static int ParsePort(string port) => int.TryParse(port, out var value) ? value : 0;

    // Address comparisons are done on the trimmed, lowercased form. Stored rows keep whatever
    // they were written with, so normalizing here never rewrites data the user already has.
    private static string NormalizeAliasAddress(string address)
        => address?.Trim().ToLowerInvariant() ?? string.Empty;

    private readonly SemaphoreSlim _aliasWriteLock = new(1, 1);

    public async Task UpdateAccountAliasesAsync(Guid accountId, List<MailAccountAlias> aliases)
    {
        // One transaction: a failed insert must not leave the account with its aliases deleted.
        await Connection.RunInTransactionAsync(connection =>
        {
            connection.Table<MailAccountAlias>().Delete(a => a.AccountId == accountId);

            foreach (var alias in aliases)
            {
                connection.Insert(alias, typeof(MailAccountAlias));
            }
        }).ConfigureAwait(false);
    }

    public async Task<bool> AddAccountAliasAsync(Guid accountId, MailAccountAlias alias)
    {
        Guard.IsNotNull(alias);

        var normalizedAddress = NormalizeAliasAddress(alias.AliasAddress);
        if (string.IsNullOrEmpty(normalizedAddress))
            return false;

        // Check and insert under one lock, so two adds of the same address cannot both pass.
        await _aliasWriteLock.WaitAsync().ConfigureAwait(false);

        try
        {
            var existing = await GetAccountAliasesAsync(accountId).ConfigureAwait(false);
            if (existing.Any(a => NormalizeAliasAddress(a.AliasAddress) == normalizedAddress))
                return false;

            alias.AccountId = accountId;
            alias.AliasAddress = normalizedAddress;
            if (alias.Id == Guid.Empty) alias.Id = Guid.NewGuid();

            await Connection.InsertAsync(alias, typeof(MailAccountAlias)).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _aliasWriteLock.Release();
        }
    }

    public async Task SetDefaultAccountAliasAsync(Guid accountId, Guid aliasId)
    {
        var aliases = await GetAccountAliasesAsync(accountId).ConfigureAwait(false);
        var target = aliases.Find(a => a.Id == aliasId)
            ?? throw new InvalidOperationException($"Alias {aliasId} does not belong to account {accountId}.");

        // Only the primary flag moves. Root aliases and everything else stay as they are.
        foreach (var alias in aliases.Where(a => a.IsPrimary && a.Id != target.Id))
        {
            alias.IsPrimary = false;
            await Connection.UpdateAsync(alias, typeof(MailAccountAlias)).ConfigureAwait(false);
        }

        if (!target.IsPrimary)
        {
            target.IsPrimary = true;
            await Connection.UpdateAsync(target, typeof(MailAccountAlias)).ConfigureAwait(false);
        }
    }

    public async Task SetAliasSigningCertificateAsync(Guid accountId, Guid aliasId, string thumbprint)
    {
        var alias = await GetOwnedAliasAsync(accountId, aliasId).ConfigureAwait(false);

        alias.SelectedSigningCertificateThumbprint = string.IsNullOrWhiteSpace(thumbprint) ? null : thumbprint;
        await Connection.UpdateAsync(alias, typeof(MailAccountAlias)).ConfigureAwait(false);
    }

    public async Task SetAliasEncryptionAsync(Guid accountId, Guid aliasId, bool isEnabled)
    {
        var alias = await GetOwnedAliasAsync(accountId, aliasId).ConfigureAwait(false);

        alias.IsSmimeEncryptionEnabled = isEnabled;
        await Connection.UpdateAsync(alias, typeof(MailAccountAlias)).ConfigureAwait(false);
    }

    private async Task<MailAccountAlias> GetOwnedAliasAsync(Guid accountId, Guid aliasId)
    {
        var aliases = await GetAccountAliasesAsync(accountId).ConfigureAwait(false);

        return aliases.Find(a => a.Id == aliasId)
            ?? throw new InvalidOperationException($"Alias {aliasId} does not belong to account {accountId}.");
    }

    public async Task UpdateRemoteAliasInformationAsync(MailAccount account, List<RemoteAccountAlias> remoteAccountAliases)
    {
        var localAliases = await GetAccountAliasesAsync(account.Id).ConfigureAwait(false);
        var knownBeforeRefresh = localAliases.Select(a => a.Id).ToHashSet();
        var normalizedRemoteAliases = remoteAccountAliases ?? [];

        // The whole refresh commits or none of it does: a failed insert must not leave earlier
        // rows carrying half of the provider's answer.
        await Connection.RunInTransactionAsync(connection =>
        {
            foreach (var remoteAlias in normalizedRemoteAliases)
            {
                if (string.IsNullOrWhiteSpace(remoteAlias?.AliasAddress))
                    continue;

                var normalizedAddress = NormalizeAliasAddress(remoteAlias.AliasAddress);
                var existingAlias = localAliases.Find(a => NormalizeAliasAddress(a.AliasAddress) == normalizedAddress);

                if (existingAlias == null)
                {
                    // A new alias never arrives as the default. Which alias is the default is a
                    // local choice, and the provider's answer must not silently replace it.
                    var newAlias = new MailAccountAlias()
                    {
                        AccountId = account.Id,
                        AliasAddress = remoteAlias.AliasAddress,
                        IsPrimary = false,
                        IsRootAlias = false,
                        IsVerified = remoteAlias.IsVerified,
                        ReplyToAddress = remoteAlias.ReplyToAddress,
                        Id = Guid.NewGuid(),
                        AliasSenderName = remoteAlias.AliasSenderName,
                        Source = remoteAlias.Source,
                        SendCapability = remoteAlias.SendCapability
                    };

                    connection.Insert(newAlias, typeof(MailAccountAlias));
                    localAliases.Add(newAlias);
                }
                else
                {
                    // Provider-owned fields only. The primary and root flags belong to this
                    // device, so a refresh leaves them exactly as it found them.
                    existingAlias.IsVerified = remoteAlias.IsVerified;
                    existingAlias.ReplyToAddress = remoteAlias.ReplyToAddress;
                    existingAlias.AliasSenderName = remoteAlias.AliasSenderName;
                    existingAlias.Source = remoteAlias.Source;
                    existingAlias.SendCapability = ResolveSendCapability(existingAlias.SendCapability, remoteAlias.SendCapability);

                    connection.Update(existingAlias, typeof(MailAccountAlias));
                }
            }

            if (localAliases.Count == 0 && !string.IsNullOrWhiteSpace(account.Address))
            {
                var fallbackAddress = account.Address.Trim();
                var fallbackAlias = new MailAccountAlias()
                {
                    AccountId = account.Id,
                    AliasAddress = fallbackAddress,
                    IsPrimary = true,
                    IsRootAlias = true,
                    IsVerified = true,
                    ReplyToAddress = fallbackAddress,
                    Id = Guid.NewGuid(),
                    Source = AliasSource.ProviderDiscovered,
                    SendCapability = AliasSendCapability.Confirmed
                };

                connection.Insert(fallbackAlias, typeof(MailAccountAlias));
                localAliases.Add(fallbackAlias);
            }

            // Only the complete absence of a default is repaired here. Several rows flagged
            // primary is a legacy shape, and picking a winner for the user would be a guess;
            // it is resolved the moment they choose one.
            if (!localAliases.Any(a => a.IsPrimary))
            {
                // An alias that was already here outranks one this refresh just added.
                var candidates = localAliases.Where(a => knownBeforeRefresh.Contains(a.Id)).ToList();
                if (candidates.Count == 0) candidates = localAliases;

                var idealPrimaryAlias = candidates.Find(a =>
                    NormalizeAliasAddress(a.AliasAddress) == NormalizeAliasAddress(account.Address)) ?? candidates[0];

                idealPrimaryAlias.IsPrimary = true;
                connection.Update(idealPrimaryAlias, typeof(MailAccountAlias));
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// A provider that cannot say keeps the answer it gave before. Losing a recorded denial to
    /// "unknown" would make Wino offer a sender the provider already refused.
    /// </summary>
    private static AliasSendCapability ResolveSendCapability(AliasSendCapability local, AliasSendCapability remote)
        => remote == AliasSendCapability.Unknown ? local : remote;

    public async Task UpdateAliasSendCapabilityAsync(Guid accountId, string aliasAddress, AliasSendCapability capability)
    {
        if (string.IsNullOrWhiteSpace(aliasAddress))
            return;

        var aliases = await GetAccountAliasesAsync(accountId).ConfigureAwait(false);
        var alias = aliases.FirstOrDefault(a => a.AliasAddress.Equals(aliasAddress, StringComparison.OrdinalIgnoreCase));

        if (alias == null)
            return;

        alias.SendCapability = capability;
        await Connection.UpdateAsync(alias, typeof(MailAccountAlias)).ConfigureAwait(false);
    }

    public async Task DeleteAccountAliasAsync(Guid aliasId)
    {
        // Create query to delete alias.

        await Connection.ExecuteAsync("DELETE FROM MailAccountAlias WHERE Id = ?", aliasId).ConfigureAwait(false);
    }

    public async Task CreateAccountAsync(
        MailAccount account,
        CustomServerInformation? customServerInformation,
        bool shouldAppendMessagesToSentFolder = true,
        bool enableMailFilters = false)
    {
        Guard.IsNotNull(account);

        if (await AccountNameExistsAsync(account.Name).ConfigureAwait(false))
            throw new InvalidOperationException(Translator.DialogMessage_AccountNameExistsMessage);

        if (await AccountAddressExistsAsync(account.Address).ConfigureAwait(false))
            throw new InvalidOperationException(Translator.DialogMessage_AccountAddressExistsMessage);

        if (!account.CreatedAt.HasValue)
        {
            account.CreatedAt = DateTime.UtcNow;
        }

        var accountCount = await Connection.Table<MailAccount>().CountAsync();

        // If there are no accounts before this one, set it as startup account.
        if (accountCount == 0)
        {
            _preferencesService.StartupEntityId = account.Id;
        }
        else
        {
            // Set the order of the account.
            // This can be changed by the user later in manage accounts page.
            account.Order = accountCount;
        }

        await Connection.InsertAsync(account, typeof(MailAccount));

        if (enableMailFilters &&
            account.IsMailAccessGranted &&
            account.ProviderType is MailProviderType.Gmail or MailProviderType.Outlook)
        {
            var authorizedAt = DateTime.UtcNow;
            await Connection.InsertAsync(new AccountProviderFeature
            {
                Id = Guid.NewGuid(),
                MailAccountId = account.Id,
                Feature = ProviderFeature.MailFilters,
                AuthorizationState = ProviderFeatureAuthorizationState.Active,
                EnabledAtUtc = authorizedAt,
                LastAuthorizedAtUtc = authorizedAt
            }, typeof(AccountProviderFeature)).ConfigureAwait(false);
        }

        if (account.IsTaskAccessEnabled && !account.IsTaskAccessGranted)
        {
            var taskListColors = (await Connection.Table<AccountTaskList>().ToListAsync().ConfigureAwait(false))
                .Select(list => list.ColorHex);
            await Connection.InsertAsync(new AccountTaskList
            {
                Id = Guid.NewGuid(),
                MailAccountId = account.Id,
                SourceKind = TaskSourceKind.Local,
                Title = string.IsNullOrWhiteSpace(account.Name) ? "Tasks" : account.Name,
                ColorHex = ColorPalette.GetDistinctColor(taskListColors),
                IsDefault = true,
                PendingMutation = TaskPendingMutation.None
            }, typeof(AccountTaskList)).ConfigureAwait(false);
        }

        if (account.IsContactAccessEnabled && !account.IsContactAccessGranted)
        {
            await Connection.InsertAsync(new ContactAddressBook
            {
                Id = Guid.NewGuid(),
                MailAccountId = account.Id,
                SourceKind = ContactSourceKind.Local,
                DisplayName = account.Name,
                IsDefault = true
            }, typeof(ContactAddressBook)).ConfigureAwait(false);
        }

        var preferences = new MailAccountPreferences()
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            IsNotificationsEnabled = true,
            ShouldAppendMessagesToSentFolder = shouldAppendMessagesToSentFolder
        };

        account.Preferences = preferences;

        // Outlook & Office 365 supports Focused inbox. Enabled by default.
        bool isMicrosoftProvider = account.ProviderType == MailProviderType.Outlook;

        // TODO: This should come from account settings API.
        // Wino doesn't have MailboxSettings yet.
        if (isMicrosoftProvider)
            account.Preferences.IsFocusedInboxEnabled = true;

        // Setup default signature.
        var defaultSignature = await _signatureService.CreateDefaultSignatureAsync(account.Id);

        account.Preferences.SignatureIdForNewMessages = defaultSignature.Id;
        account.Preferences.SignatureIdForFollowingMessages = defaultSignature.Id;
        account.Preferences.IsSignatureEnabled = true;

        await Connection.InsertAsync(preferences, typeof(MailAccountPreferences));

        if (customServerInformation != null)
        {
            customServerInformation.AccountId = account.Id;
            await Connection.InsertAsync(customServerInformation, typeof(CustomServerInformation));
            await _serverCertificateTrustService.SaveTrustsAsync(account.Id, customServerInformation.PendingCertificateTrusts).ConfigureAwait(false);
            customServerInformation.PendingCertificateTrusts.Clear();
        }

        if (account.ProviderType == MailProviderType.POP3)
        {
            await EnsurePop3LocalFoldersAsync(account.Id).ConfigureAwait(false);
        }

        var shouldCreateLocalCalendar = account.ProviderType.IsCustomMailProvider()
            ? customServerInformation?.CalendarSupportMode == ImapCalendarSupportMode.LocalOnly
            : account.IsCalendarAccessEnabled && !account.IsCalendarAccessGranted;

        if (shouldCreateLocalCalendar)
        {
            await EnsureDefaultLocalCalendarAsync(account.Id).ConfigureAwait(false);
        }
    }

    private async Task EnsurePop3LocalFoldersAsync(Guid accountId)
    {
        var existing = await Connection.Table<MailItemFolder>()
            .Where(folder => folder.MailAccountId == accountId)
            .ToListAsync()
            .ConfigureAwait(false);

        var definitions = new (string Id, string Name, SpecialFolderType Type)[]
        {
            ("local-inbox", Translator.POP3Folder_Inbox, SpecialFolderType.Inbox),
            ("local-drafts", Translator.POP3Folder_Drafts, SpecialFolderType.Draft),
            ("local-sent", Translator.POP3Folder_Sent, SpecialFolderType.Sent),
            ("local-archive", Translator.POP3Folder_Archive, SpecialFolderType.Archive),
            ("local-deleted", Translator.POP3Folder_Deleted, SpecialFolderType.Deleted)
        };

        foreach (var definition in definitions)
        {
            if (existing.Any(folder => folder.SpecialFolderType == definition.Type))
                continue;

            await Connection.InsertAsync(new MailItemFolder
            {
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                RemoteFolderId = definition.Id,
                FolderName = definition.Name,
                SpecialFolderType = definition.Type,
                IsSystemFolder = true,
                IsSticky = true,
                IsSynchronizationEnabled = definition.Type == SpecialFolderType.Inbox,
                ShowUnreadCount = definition.Type != SpecialFolderType.Deleted
            }, typeof(MailItemFolder)).ConfigureAwait(false);
        }
    }

    private async Task EnsureDefaultLocalCalendarAsync(Guid accountId)
    {
        var existingCalendarCount = await Connection.Table<AccountCalendar>()
            .Where(a => a.AccountId == accountId)
            .CountAsync()
            .ConfigureAwait(false);

        if (existingCalendarCount > 0)
            return;

        var localCalendar = new AccountCalendar
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Name = Translator.AccountDetailsPage_TabCalendar,
            IsPrimary = true,
            IsSynchronizationEnabled = true,
            IsExtended = true,
            RemoteCalendarId = string.Empty,
            TimeZone = string.Empty,
            BackgroundColorHex = await GetNextDistinctCalendarColorAsync().ConfigureAwait(false)
        };

        localCalendar.TextColorHex = GetReadableTextColorHex(localCalendar.BackgroundColorHex);

        await Connection.InsertAsync(localCalendar, typeof(AccountCalendar)).ConfigureAwait(false);
    }

    private async Task<string> GetNextDistinctCalendarColorAsync()
    {
        var usedColors = await Connection.Table<AccountCalendar>()
            .ToListAsync()
            .ConfigureAwait(false);

        return ColorPalette.GetDistinctColor(usedColors.Select(a => a.BackgroundColorHex));
    }

    private static string GetReadableTextColorHex(string backgroundColorHex)
    {
        if (!TryParseHexColor(backgroundColorHex, out var red, out var green, out var blue))
            return "#FFFFFF";

        var luminance = ((0.299 * red) + (0.587 * green) + (0.114 * blue)) / 255d;
        return luminance > 0.6 ? "#111111" : "#FFFFFF";
    }

    private static bool TryParseHexColor(string value, out int red, out int green, out int blue)
    {
        red = 255;
        green = 255;
        blue = 255;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var color = value.Trim();
        if (color.StartsWith('#'))
        {
            color = color[1..];
        }

        if (color.Length != 6 ||
            !int.TryParse(color, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        red = Convert.ToInt32(color.Substring(0, 2), 16);
        green = Convert.ToInt32(color.Substring(2, 2), 16);
        blue = Convert.ToInt32(color.Substring(4, 2), 16);
        return true;
    }

    public async Task UpdateAccountOrdersAsync(Dictionary<Guid, int> accountIdOrderPair)
    {
        foreach (var pair in accountIdOrderPair)
        {
            var account = await GetAccountAsync(pair.Key);

            if (account == null)
            {
                _logger.Information("Could not find account with id {Key} for reordering. It may be a linked account.", pair.Key);
                continue;
            }

            account.Order = pair.Value;

            await Connection.UpdateAsync(account, typeof(MailAccount));
        }

        Messenger.Send(new AccountMenuItemsReordered(accountIdOrderPair));
    }

    public async Task<MailAccountAlias> GetPrimaryAccountAliasAsync(Guid accountId)
    {
        var aliases = await GetAccountAliasesAsync(accountId);

        if (aliases == null || aliases.Count == 0) return null;

        return aliases.FirstOrDefault(a => a.IsPrimary) ?? aliases.First();
    }

    public async Task<bool> IsAccountFocusedEnabledAsync(Guid accountId)
    {
        var account = await GetAccountAsync(accountId);
        return account.Preferences.IsFocusedInboxEnabled.GetValueOrDefault();
    }

    public async Task<bool> IsNotificationsEnabled(Guid accountId)
    {
        var account = await GetAccountAsync(accountId);

        return account?.Preferences?.IsNotificationsEnabled ?? false;
    }

    public async Task UpdateLastFolderStructureSyncDateAsync(Guid accountId)
    {
        var account = await GetAccountAsync(accountId);
        if (account == null) return;

        account.LastFolderStructureSyncDate = DateTime.UtcNow;
        await Connection.UpdateAsync(account, typeof(MailAccount)).ConfigureAwait(false);
    }

    public async Task<bool> ShouldSyncFolderStructureAsync(Guid accountId, TimeSpan syncInterval)
    {
        var account = await GetAccountAsync(accountId);
        if (account == null) return true;

        if (!account.LastFolderStructureSyncDate.HasValue)
            return true;

        return DateTime.UtcNow - account.LastFolderStructureSyncDate.Value > syncInterval;
    }
}
