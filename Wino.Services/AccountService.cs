using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Messaging.Client.Accounts;
using Wino.Messaging.UI;

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
        using var context = ContextFactory.CreateDbContext();

        var account = await GetAccountAsync(accountId);

        Guard.IsNotNull(account);

        account.AttentionReason = AccountAttentionReason.None;

        await UpdateAccountAsync(account);
    }

    public async Task UpdateMergedInboxAsync(Guid mergedInboxId, IEnumerable<Guid> linkedAccountIds)
    {
        using var context = ContextFactory.CreateDbContext();

        // First, remove all accounts from merged inbox.
        await context.MailAccounts
            .Where(a => a.MergedInboxId == mergedInboxId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.MergedInboxId, (Guid?)null));

        // Then, add new accounts to merged inbox.
        await context.MailAccounts
            .Where(a => linkedAccountIds.Contains(a.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.MergedInboxId, mergedInboxId));

        WeakReferenceMessenger.Default.Send(new AccountsMenuRefreshRequested());
    }

    public async Task<string> UpdateSyncIdentifierRawAsync(Guid accountId, string syncIdentifier)
    {
        using var context = ContextFactory.CreateDbContext();

        await context.MailAccounts
            .Where(a => a.Id == accountId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.SynchronizationDeltaIdentifier, syncIdentifier));

        return syncIdentifier;
    }

    public async Task UnlinkMergedInboxAsync(Guid mergedInboxId)
    {
        using var context = ContextFactory.CreateDbContext();

        var mergedInbox = await context.MergedInboxes.FirstOrDefaultAsync(a => a.Id == mergedInboxId).ConfigureAwait(false);

        if (mergedInbox == null)
        {
            _logger.Warning("Could not find merged inbox with id {MergedInboxId}", mergedInboxId);

            return;
        }

        await context.MailAccounts
            .Where(a => a.MergedInboxId == mergedInboxId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.MergedInboxId, (Guid?)null))
            .ConfigureAwait(false);

        context.MergedInboxes.Remove(mergedInbox);
        await context.SaveChangesAsync().ConfigureAwait(false);

        // Change the startup entity id if it was the merged inbox.
        // Take the first account as startup account.

        if (_preferencesService.StartupEntityId == mergedInboxId)
        {
            var firstAccount = await context.MailAccounts.FirstOrDefaultAsync();

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

        using var context = ContextFactory.CreateDbContext();

        // 0. Give the merged inbox a new Guid.
        mergedInbox.Id = Guid.NewGuid();

        var accountFolderDictionary = new Dictionary<MailAccount, List<MailItemFolder>>();

        // 1. Make all folders in the accounts unsticky. We will stick them based on common special folder types.
        foreach (var account in accountsToMerge)
        {
            var accountFolderList = new List<MailItemFolder>();

            var folders = await context.MailItemFolders.Where(a => a.MailAccountId == account.Id).ToListAsync();

            foreach (var folder in folders)
            {
                accountFolderList.Add(folder);
                folder.IsSticky = false;

                context.MailItemFolders.Update(folder);
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

                        context.MailItemFolders.Update(folder);
                    }
                }
            }
        }

        // 3. Insert merged inbox and assign accounts.
        context.MergedInboxes.Add(mergedInbox);

        foreach (var account in accountsToMerge)
        {
            account.MergedInboxId = mergedInbox.Id;

            context.MailAccounts.Update(account);
        }

        await context.SaveChangesAsync();

        WeakReferenceMessenger.Default.Send(new AccountsMenuRefreshRequested());
    }

    public async Task RenameMergedAccountAsync(Guid mergedInboxId, string newName)
    {
        using var context = ContextFactory.CreateDbContext();

        await context.MergedInboxes
            .Where(a => a.Id == mergedInboxId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.Name, newName));

        ReportUIChange(new MergedInboxRenamed(mergedInboxId, newName));
    }

    public async Task FixTokenIssuesAsync(Guid accountId)
    {
        using var context = ContextFactory.CreateDbContext();

        var account = await context.MailAccounts.FirstOrDefaultAsync(a => a.Id == accountId);

        if (account == null) return;

        //var authenticator = _authenticationProvider.GetAuthenticator(account.ProviderType);

        //// This will re-generate token.
        //var token = await authenticator.GenerateTokenInformationAsync(account);

        // TODO: Rest?
        // Guard.IsNotNull(token);
    }

    public async Task<List<MailAccount>> GetAccountsAsync()
    {
        using var context = ContextFactory.CreateDbContext();

        var accounts = await context.MailAccounts
            .OrderBy(a => a.Order)
            .Include(a => a.Preferences)
            .Include(a => a.ServerInformation)
            .Include(a => a.MergedInbox)
            .ToListAsync();

        return accounts;
    }

    public async Task CreateRootAliasAsync(Guid accountId, string address)
    {
        using var context = ContextFactory.CreateDbContext();

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

        context.MailAccountAliases.Add(rootAlias);
        await context.SaveChangesAsync().ConfigureAwait(false);

        Log.Information("Created root alias for the account {AccountId}", accountId);
    }

    public async Task<List<MailAccountAlias>> GetAccountAliasesAsync(Guid accountId)
    {
        using var context = ContextFactory.CreateDbContext();

        return await context.MailAccountAliases
            .Where(a => a.AccountId == accountId)
            .OrderByDescending(a => a.IsRootAlias)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task DeleteAccountMailCacheAsync(Guid accountId, AccountCacheResetReason accountCacheResetReason)
    {
        using var context = ContextFactory.CreateDbContext();

        // Delete all mail copies for folders belonging to this account
        var folderIds = await context.MailItemFolders
            .Where(f => f.MailAccountId == accountId)
            .Select(f => f.Id)
            .ToListAsync();

        await context.MailCopies
            .Where(mc => folderIds.Contains(mc.FolderId))
            .ExecuteDeleteAsync();

        WeakReferenceMessenger.Default.Send(new AccountCacheResetMessage(accountId, accountCacheResetReason));
    }

    public async Task DeleteAccountAsync(MailAccount account)
    {
        using var context = ContextFactory.CreateDbContext();

        await DeleteAccountMailCacheAsync(account.Id, AccountCacheResetReason.AccountRemoval);

        await context.MailItemFolders.Where(a => a.MailAccountId == account.Id).ExecuteDeleteAsync();
        await context.AccountSignatures.Where(a => a.MailAccountId == account.Id).ExecuteDeleteAsync();
        await context.MailAccountAliases.Where(a => a.AccountId == account.Id).ExecuteDeleteAsync();

        // Account belongs to a merged inbox.
        // In case of there'll be a single account in the merged inbox, remove the merged inbox as well.

        if (account.MergedInboxId != null)
        {
            var mergedInboxAccountCount = await context.MailAccounts
                .Where(a => a.MergedInboxId == account.MergedInboxId.Value)
                .CountAsync();

            // There will be only one account in the merged inbox. Remove the link for the other account as well.
            if (mergedInboxAccountCount == 2)
            {
                await context.MailAccounts
                    .Where(a => a.MergedInboxId == account.MergedInboxId.Value)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.MergedInboxId, (Guid?)null))
                    .ConfigureAwait(false);
            }
        }

        if (account.ProviderType == MailProviderType.IMAP4)
            await context.CustomServerInformations.Where(a => a.AccountId == account.Id).ExecuteDeleteAsync();

        if (account.Preferences != null)
        {
            context.MailAccountPreferences.Remove(account.Preferences);
        }

        context.MailAccounts.Remove(account);
        await context.SaveChangesAsync();

        await _mimeFileService.DeleteUserMimeCacheAsync(account.Id).ConfigureAwait(false);

        // Clear out or set up a new startup entity id.
        // Next account after the deleted one will be the startup account.

        if (_preferencesService.StartupEntityId == account.Id || _preferencesService.StartupEntityId == account.MergedInboxId)
        {
            var firstNonStartupAccount = await context.MailAccounts.FirstOrDefaultAsync(a => a.Id != account.Id);

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
        using var context = ContextFactory.CreateDbContext();

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

            var existingContact = await context.AccountContacts.FirstOrDefaultAsync(a => a.Address == accountContact.Address).ConfigureAwait(false);

            if (existingContact != null)
            {
                existingContact.Name = accountContact.Name;
                existingContact.Base64ContactPicture = accountContact.Base64ContactPicture;
                existingContact.IsRootContact = accountContact.IsRootContact;
                context.AccountContacts.Update(existingContact);
            }
            else
            {
                context.AccountContacts.Add(accountContact);
            }

            await context.SaveChangesAsync().ConfigureAwait(false);

            await UpdateAccountAsync(account).ConfigureAwait(false);
        }
    }

    public async Task<MailAccount> GetAccountAsync(Guid accountId)
    {
        using var context = ContextFactory.CreateDbContext();

        var account = await context.MailAccounts
            .Include(a => a.Preferences)
            .Include(a => a.ServerInformation)
            .Include(a => a.MergedInbox)
            .FirstOrDefaultAsync(a => a.Id == accountId);

        if (account == null)
        {
            _logger.Error("Could not find account with id {AccountId}", accountId);
        }

        return account;
    }

    public async Task<CustomServerInformation> GetAccountCustomServerInformationAsync(Guid accountId)
    {
        using var context = ContextFactory.CreateDbContext();
        return await context.CustomServerInformations.FirstOrDefaultAsync(a => a.AccountId == accountId);
    }

    public async Task UpdateAccountAsync(MailAccount account)
    {
        using var context = ContextFactory.CreateDbContext();

        context.MailAccountPreferences.Update(account.Preferences);
        context.MailAccounts.Update(account);

        await context.SaveChangesAsync().ConfigureAwait(false);

        ReportUIChange(new AccountUpdatedMessage(account));
    }

    public async Task UpdateAccountCustomServerInformationAsync(CustomServerInformation customServerInformation)
    {
        using var context = ContextFactory.CreateDbContext();

        context.CustomServerInformations.Update(customServerInformation);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task UpdateAccountAliasesAsync(Guid accountId, List<MailAccountAlias> aliases)
    {
        using var context = ContextFactory.CreateDbContext();

        // Delete existing ones.
        await context.MailAccountAliases.Where(a => a.AccountId == accountId).ExecuteDeleteAsync().ConfigureAwait(false);

        // Insert new ones.
        context.MailAccountAliases.AddRange(aliases);
        await context.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task UpdateRemoteAliasInformationAsync(MailAccount account, List<RemoteAccountAlias> remoteAccountAliases)
    {
        using var context = ContextFactory.CreateDbContext();

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

                context.MailAccountAliases.Add(newAlias);
                localAliases.Add(newAlias);
            }
            else
            {
                // Update existing alias.
                existingAlias.IsPrimary = remoteAlias.IsPrimary;
                existingAlias.IsVerified = remoteAlias.IsVerified;
                existingAlias.ReplyToAddress = remoteAlias.ReplyToAddress;
                existingAlias.AliasSenderName = remoteAlias.AliasSenderName;

                context.MailAccountAliases.Update(existingAlias);
            }
        }

        await context.SaveChangesAsync();

        // Make sure there is only 1 root alias and 1 primary alias selected.

        bool shouldUpdatePrimary = localAliases.Count(a => a.IsPrimary) != 1;
        bool shouldUpdateRoot = localAliases.Count(a => a.IsRootAlias) != 1;

        if (shouldUpdatePrimary)
        {
            localAliases.ForEach(a => a.IsPrimary = false);

            var idealPrimaryAlias = localAliases.Find(a => a.AliasAddress == account.Address) ?? localAliases.First();

            idealPrimaryAlias.IsPrimary = true;
            context.MailAccountAliases.Update(idealPrimaryAlias);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        if (shouldUpdateRoot)
        {
            localAliases.ForEach(a => a.IsRootAlias = false);

            var idealRootAlias = localAliases.Find(a => a.AliasAddress == account.Address) ?? localAliases.First();

            idealRootAlias.IsRootAlias = true;
            context.MailAccountAliases.Update(idealRootAlias);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    public async Task DeleteAccountAliasAsync(Guid aliasId)
    {
        using var context = ContextFactory.CreateDbContext();

        await context.MailAccountAliases
            .Where(a => a.Id == aliasId)
            .ExecuteDeleteAsync()
            .ConfigureAwait(false);
    }

    public async Task CreateAccountAsync(MailAccount account, CustomServerInformation customServerInformation)
    {
        Guard.IsNotNull(account);

        using var context = ContextFactory.CreateDbContext();

        var accountCount = await context.MailAccounts.CountAsync();

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

        context.MailAccounts.Add(account);

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
        var defaultSignature = _signatureService.GetDefaultSignatureAsync(account.Id);
        context.AccountSignatures.Add(defaultSignature);

        account.Preferences.SignatureIdForNewMessages = defaultSignature.Id;
        account.Preferences.SignatureIdForFollowingMessages = defaultSignature.Id;
        account.Preferences.IsSignatureEnabled = true;

        context.MailAccountPreferences.Add(preferences);

        if (customServerInformation != null)
            context.CustomServerInformations.Add(customServerInformation);

        await context.SaveChangesAsync();
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
        using var context = ContextFactory.CreateDbContext();

        foreach (var pair in accountIdOrderPair)
        {
            var account = await GetAccountAsync(pair.Key);

            if (account == null)
            {
                _logger.Information("Could not find account with id {Key} for reordering. It may be a linked account.", pair.Key);
                continue;
            }

            account.Order = pair.Value;

            context.MailAccounts.Update(account);
        }

        await context.SaveChangesAsync();

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
