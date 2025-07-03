using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SqlKata;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.UI;
using Wino.Services.Extensions;

namespace Wino.Services;

public class AccountService : BaseDatabaseService, IAccountService
{
    public IAuthenticator ExternalAuthenticationAuthenticator { get; set; }

    private readonly ISignatureService _signatureService;
    private readonly IMimeFileService _mimeFileService;
    private readonly IPreferencesService _preferencesService;

    private readonly ILogger _logger = Log.ForContext<AccountService>();

    public AccountService(IDatabaseService databaseService,
                          ISignatureService signatureService,
                          IMimeFileService mimeFileService,
                          IPreferencesService preferencesService) : base(databaseService)
    {
        _signatureService = signatureService;
        _mimeFileService = mimeFileService;
        _preferencesService = preferencesService;
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
        var query = new Query("MailAccount")
            .WhereIn("Id", linkedAccountIds)
            .AsUpdate(new
            {
                MergedInboxId = mergedInboxId
            });

        await Connection.ExecuteAsync(query.GetRawQuery());

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

        var query = new Query("MailAccount")
            .Where("MergedInboxId", mergedInboxId)
            .AsUpdate(new
            {
                MergedInboxId = (Guid?)null
            });

        await Connection.ExecuteAsync(query.GetRawQuery()).ConfigureAwait(false);
        await Connection.DeleteAsync(mergedInbox).ConfigureAwait(false);

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

                await Connection.UpdateAsync(folder);
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

                        await Connection.UpdateAsync(folder);
                    }
                }
            }
        }

        // 3. Insert merged inbox and assign accounts.
        await Connection.InsertAsync(mergedInbox);

        foreach (var account in accountsToMerge)
        {
            account.MergedInboxId = mergedInbox.Id;

            await Connection.UpdateAsync(account);
        }

        WeakReferenceMessenger.Default.Send(new AccountsMenuRefreshRequested());
    }

    public async Task RenameMergedAccountAsync(Guid mergedInboxId, string newName)
    {
        var query = new Query("MergedInbox")
            .Where("Id", mergedInboxId)
            .AsUpdate(new
            {
                Name = newName
            });

        await Connection.ExecuteAsync(query.GetRawQuery());

        ReportUIChange(new MergedInboxRenamed(mergedInboxId, newName));
    }

    public async Task FixTokenIssuesAsync(Guid accountId)
    {
        var account = await Connection.Table<MailAccount>().FirstOrDefaultAsync(a => a.Id == accountId);

        if (account == null) return;

        //var authenticator = _authenticationProvider.GetAuthenticator(account.ProviderType);

        //// This will re-generate token.
        //var token = await authenticator.GenerateTokenInformationAsync(account);

        // TODO: Rest?
        // Guard.IsNotNull(token);
    }

    private Task<MailAccountPreferences> GetAccountPreferencesAsync(Guid accountId)
        => Connection.Table<MailAccountPreferences>().FirstOrDefaultAsync(a => a.AccountId == accountId);

    public async Task<List<MailAccount>> GetAccountsAsync()
    {
        var accounts = await Connection.Table<MailAccount>().OrderBy(a => a.Order).ToListAsync();

        foreach (var account in accounts)
        {
            // Load IMAP server configuration.
            if (account.ProviderType == MailProviderType.IMAP4)
                account.ServerInformation = await GetAccountCustomServerInformationAsync(account.Id);

            // Load MergedInbox information.
            if (account.MergedInboxId != null)
                account.MergedInbox = await GetMergedInboxInformationAsync(account.MergedInboxId.Value);

            account.Preferences = await GetAccountPreferencesAsync(account.Id);
        }

        return accounts;
    }

    public async Task CreateRootAliasAsync(Guid accountId, string address)
    {
        var rootAlias = new MailAccountAlias()
        {
            AccountId = accountId,
            AliasAddress = address,
            IsPrimary = true,
            IsRootAlias = true,
            IsVerified = true,
            ReplyToAddress = address,
            Id = Guid.NewGuid()
        };

        await Connection.InsertAsync(rootAlias).ConfigureAwait(false);

        Log.Information("Created root alias for the account {AccountId}", accountId);
    }

    public async Task<List<MailAccountAlias>> GetAccountAliasesAsync(Guid accountId)
    {
        var query = new Query(nameof(MailAccountAlias))
            .Where(nameof(MailAccountAlias.AccountId), accountId)
            .OrderByDesc(nameof(MailAccountAlias.IsRootAlias));

        return await Connection.QueryAsync<MailAccountAlias>(query.GetRawQuery()).ConfigureAwait(false);
    }

    private Task<MergedInbox> GetMergedInboxInformationAsync(Guid mergedInboxId)
        => Connection.Table<MergedInbox>().FirstOrDefaultAsync(a => a.Id == mergedInboxId);

    public async Task DeleteAccountMailCacheAsync(Guid accountId, AccountCacheResetReason accountCacheResetReason)
    {
        var deleteQuery = new Query("MailCopy")
                .WhereIn("Id", q => q
                .From("MailCopy")
                .Select("Id")
                .WhereIn("FolderId", q2 => q2
                    .From("MailItemFolder")
                    .Select("Id")
                    .Where("MailAccountId", accountId)
                )).AsDelete();

        await Connection.ExecuteAsync(deleteQuery.GetRawQuery());

        WeakReferenceMessenger.Default.Send(new AccountCacheResetMessage(accountId, accountCacheResetReason));
    }

    public async Task DeleteAccountAsync(MailAccount account)
    {
        await DeleteAccountMailCacheAsync(account.Id, AccountCacheResetReason.AccountRemoval);

        await Connection.Table<MailItemFolder>().DeleteAsync(a => a.MailAccountId == account.Id);
        await Connection.Table<AccountSignature>().DeleteAsync(a => a.MailAccountId == account.Id);
        await Connection.Table<MailAccountAlias>().DeleteAsync(a => a.AccountId == account.Id);

        // Account belongs to a merged inbox.
        // In case of there'll be a single account in the merged inbox, remove the merged inbox as well.

        if (account.MergedInboxId != null)
        {
            var mergedInboxAccountCount = await Connection.Table<MailAccount>().Where(a => a.MergedInboxId == account.MergedInboxId.Value).CountAsync();

            // There will be only one account in the merged inbox. Remove the link for the other account as well.
            if (mergedInboxAccountCount == 2)
            {
                var query = new Query("MailAccount")
                .Where("MergedInboxId", account.MergedInboxId.Value)
                .AsUpdate(new
                {
                    MergedInboxId = (Guid?)null
                });

                await Connection.ExecuteAsync(query.GetRawQuery()).ConfigureAwait(false);
            }
        }

        if (account.ProviderType == MailProviderType.IMAP4)
            await Connection.Table<CustomServerInformation>().DeleteAsync(a => a.AccountId == account.Id);

        if (account.Preferences != null)
            await Connection.DeleteAsync(account.Preferences);

        await Connection.DeleteAsync(account);

        await _mimeFileService.DeleteUserMimeCacheAsync(account.Id).ConfigureAwait(false);

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

        ReportUIChange(new AccountRemovedMessage(account));
    }

    public async Task UpdateProfileInformationAsync(Guid accountId, ProfileInformation profileInformation)
    {
        var account = await GetAccountAsync(accountId).ConfigureAwait(false);

        if (account != null)
        {
            account.SenderName = profileInformation.SenderName;
            account.Base64ProfilePictureData = profileInformation.Base64ProfilePictureData;

            if (string.IsNullOrEmpty(account.Address))
            {
                account.Address = profileInformation.AccountAddress;
            }
            // Forcefully add or update a contact data with the provided information.

            var accountContact = new AccountContact()
            {
                Address = account.Address,
                Name = account.SenderName,
                Base64ContactPicture = account.Base64ProfilePictureData,
                IsRootContact = true
            };

            await Connection.InsertOrReplaceAsync(accountContact).ConfigureAwait(false);

            await UpdateAccountAsync(account).ConfigureAwait(false);
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
            if (account.ProviderType == MailProviderType.IMAP4)
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
        await Connection.UpdateAsync(account.Preferences).ConfigureAwait(false);
        await Connection.UpdateAsync(account).ConfigureAwait(false);

        ReportUIChange(new AccountUpdatedMessage(account));
    }

    public async Task UpdateAccountCustomServerInformationAsync(CustomServerInformation customServerInformation)
    {
        await Connection.UpdateAsync(customServerInformation).ConfigureAwait(false);
    }

    public async Task UpdateAccountAliasesAsync(Guid accountId, List<MailAccountAlias> aliases)
    {
        // Delete existing ones.
        await Connection.Table<MailAccountAlias>().DeleteAsync(a => a.AccountId == accountId).ConfigureAwait(false);

        // Insert new ones.
        foreach (var alias in aliases)
        {
            await Connection.InsertAsync(alias).ConfigureAwait(false);
        }
    }

    public async Task UpdateRemoteAliasInformationAsync(MailAccount account, List<RemoteAccountAlias> remoteAccountAliases)
    {
        var localAliases = await GetAccountAliasesAsync(account.Id).ConfigureAwait(false);
        var rootAlias = localAliases.Find(a => a.IsRootAlias);

        foreach (var remoteAlias in remoteAccountAliases)
        {
            var existingAlias = localAliases.Find(a => a.AccountId == account.Id && a.AliasAddress == remoteAlias.AliasAddress);

            if (existingAlias == null)
            {
                // Create new alias.
                var newAlias = new MailAccountAlias()
                {
                    AccountId = account.Id,
                    AliasAddress = remoteAlias.AliasAddress,
                    IsPrimary = remoteAlias.IsPrimary,
                    IsVerified = remoteAlias.IsVerified,
                    ReplyToAddress = remoteAlias.ReplyToAddress,
                    Id = Guid.NewGuid(),
                    IsRootAlias = remoteAlias.IsRootAlias,
                    AliasSenderName = remoteAlias.AliasSenderName
                };

                await Connection.InsertAsync(newAlias);
                localAliases.Add(newAlias);
            }
            else
            {
                // Update existing alias.
                existingAlias.IsPrimary = remoteAlias.IsPrimary;
                existingAlias.IsVerified = remoteAlias.IsVerified;
                existingAlias.ReplyToAddress = remoteAlias.ReplyToAddress;
                existingAlias.AliasSenderName = remoteAlias.AliasSenderName;

                await Connection.UpdateAsync(existingAlias);
            }
        }

        // Make sure there is only 1 root alias and 1 primary alias selected.

        bool shouldUpdatePrimary = localAliases.Count(a => a.IsPrimary) != 1;
        bool shouldUpdateRoot = localAliases.Count(a => a.IsRootAlias) != 1;

        if (shouldUpdatePrimary)
        {
            localAliases.ForEach(a => a.IsPrimary = false);

            var idealPrimaryAlias = localAliases.Find(a => a.AliasAddress == account.Address) ?? localAliases.First();

            idealPrimaryAlias.IsPrimary = true;
            await Connection.UpdateAsync(idealPrimaryAlias).ConfigureAwait(false);
        }

        if (shouldUpdateRoot)
        {
            localAliases.ForEach(a => a.IsRootAlias = false);

            var idealRootAlias = localAliases.Find(a => a.AliasAddress == account.Address) ?? localAliases.First();

            idealRootAlias.IsRootAlias = true;
            await Connection.UpdateAsync(idealRootAlias).ConfigureAwait(false);
        }
    }

    public async Task DeleteAccountAliasAsync(Guid aliasId)
    {
        // Create query to delete alias.

        var query = new Query("MailAccountAlias")
            .Where("Id", aliasId)
            .AsDelete();

        await Connection.ExecuteAsync(query.GetRawQuery()).ConfigureAwait(false);
    }

    public async Task CreateAccountAsync(MailAccount account, CustomServerInformation customServerInformation)
    {
        Guard.IsNotNull(account);

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

        await Connection.InsertAsync(account);

        var preferences = new MailAccountPreferences()
        {
            Id = Guid.NewGuid(),
            AccountId = account.Id,
            IsNotificationsEnabled = true,
            ShouldAppendMessagesToSentFolder = false
        };

        // iCloud does not appends sent messages to sent folder automatically.
        if (account.SpecialImapProvider == SpecialImapProvider.iCloud || account.SpecialImapProvider == SpecialImapProvider.Yahoo)
        {
            preferences.ShouldAppendMessagesToSentFolder = true;
        }

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

        await Connection.InsertAsync(preferences);

        if (customServerInformation != null)
            await Connection.InsertAsync(customServerInformation);
    }

    //public async Task<string> UpdateSynchronizationIdentifierAsync(Guid accountId, string newIdentifier)
    //{
    //    var account = await GetAccountAsync(accountId);

    //    if (account == null)
    //    {
    //        _logger.Error("Could not find account with id {AccountId}", accountId);
    //        return string.Empty;
    //    }

    //    var currentIdentifier = account.SynchronizationDeltaIdentifier;

    //    bool shouldUpdateIdentifier = account.ProviderType == MailProviderType.Gmail ?
    //            string.IsNullOrEmpty(currentIdentifier) ? true : !string.IsNullOrEmpty(currentIdentifier)
    //            && ulong.TryParse(currentIdentifier, out ulong currentIdentifierValue)
    //            && ulong.TryParse(newIdentifier, out ulong newIdentifierValue)
    //            && newIdentifierValue > currentIdentifierValue : true;

    //    if (shouldUpdateIdentifier)
    //    {
    //        account.SynchronizationDeltaIdentifier = newIdentifier;

    //        await UpdateAccountAsync(account);
    //    }

    //    return account.SynchronizationDeltaIdentifier;
    //}

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

            await Connection.UpdateAsync(account);
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
}
