using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using SqlKata;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Extensions;
using Wino.Core.Messages.Accounts;
using Wino.Core.Requests;

namespace Wino.Core.Services
{
    public class AccountService : BaseDatabaseService, IAccountService
    {
        public IAuthenticator ExternalAuthenticationAuthenticator { get; set; }

        private readonly IAuthenticationProvider _authenticationProvider;
        private readonly ISignatureService _signatureService;
        private readonly IPreferencesService _preferencesService;

        private readonly ILogger _logger = Log.ForContext<AccountService>();

        public AccountService(IDatabaseService databaseService,
                              IAuthenticationProvider authenticationProvider,
                              ISignatureService signatureService,
                              IPreferencesService preferencesService) : base(databaseService)
        {
            _authenticationProvider = authenticationProvider;
            _signatureService = signatureService;
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

            var authenticator = _authenticationProvider.GetAuthenticator(account.ProviderType);

            // This will re-generate token.
            var token = await authenticator.GenerateTokenAsync(account, true);

            Guard.IsNotNull(token);
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

        private Task<MergedInbox> GetMergedInboxInformationAsync(Guid mergedInboxId)
            => Connection.Table<MergedInbox>().FirstOrDefaultAsync(a => a.Id == mergedInboxId);

        public async Task DeleteAccountAsync(MailAccount account)
        {
            // TODO: Delete mime messages and attachments.

            await Connection.ExecuteAsync("DELETE FROM MailCopy WHERE Id IN(SELECT Id FROM MailCopy WHERE FolderId IN (SELECT Id from MailItemFolder WHERE MailAccountId == ?))", account.Id);

            await Connection.Table<TokenInformation>().Where(a => a.AccountId == account.Id).DeleteAsync();
            await Connection.Table<MailItemFolder>().DeleteAsync(a => a.MailAccountId == account.Id);

            if (account.SignatureId != null)
                await Connection.Table<AccountSignature>().DeleteAsync(a => a.Id == account.SignatureId);

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
            if (account.Preferences == null)
            {
                Debugger.Break();
            }

            await Connection.UpdateAsync(account.Preferences);
            await Connection.UpdateAsync(account);

            ReportUIChange(new AccountUpdatedMessage(account));
        }

        public async Task CreateAccountAsync(MailAccount account, TokenInformation tokenInformation, CustomServerInformation customServerInformation)
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

            account.Preferences = preferences;

            // Outlook & Office 365 supports Focused inbox. Enabled by default.
            bool isMicrosoftProvider = account.ProviderType == MailProviderType.Outlook || account.ProviderType == MailProviderType.Office365;

            // TODO: This should come from account settings API.
            // Wino doesn't have MailboxSettings yet.
            if (isMicrosoftProvider)
                account.Preferences.IsFocusedInboxEnabled = true;

            await Connection.InsertAsync(preferences);

            // Create default signature.
            var defaultSignature = await _signatureService.CreateDefaultSignatureAsync(account.Id);

            account.SignatureId = defaultSignature.Id;

            if (customServerInformation != null)
                await Connection.InsertAsync(customServerInformation);

            if (tokenInformation != null)
                await Connection.InsertAsync(tokenInformation);
        }

        public async Task<string> UpdateSynchronizationIdentifierAsync(Guid accountId, string newIdentifier)
        {
            var account = await GetAccountAsync(accountId);

            if (account == null)
            {
                _logger.Error("Could not find account with id {AccountId}", accountId);
                return string.Empty;
            }

            var currentIdentifier = account.SynchronizationDeltaIdentifier;

            bool shouldUpdateIdentifier = account.ProviderType == MailProviderType.Gmail ?
                    ((string.IsNullOrEmpty(currentIdentifier) ? true : !string.IsNullOrEmpty(currentIdentifier)
                    && ulong.TryParse(currentIdentifier, out ulong currentIdentifierValue)
                    && ulong.TryParse(newIdentifier, out ulong newIdentifierValue)
                    && newIdentifierValue > currentIdentifierValue)) : true;

            if (shouldUpdateIdentifier)
            {
                _logger.Debug("Updating synchronization identifier for {Name}. From: {SynchronizationDeltaIdentifier} To: {NewIdentifier}", account.Name, account.SynchronizationDeltaIdentifier, newIdentifier);
                account.SynchronizationDeltaIdentifier = newIdentifier;

                await UpdateAccountAsync(account);
            }

            return account.SynchronizationDeltaIdentifier;
        }

        public async Task UpdateAccountOrdersAsync(Dictionary<Guid, int> accountIdOrderPair)
        {
            foreach (var pair in accountIdOrderPair)
            {
                var account = await GetAccountAsync(pair.Key);

                if (account == null)
                {
                    _logger.Error("Could not find account with id {AccountId}", pair.Key);
                    continue;
                }

                account.Order = pair.Value;

                await Connection.UpdateAsync(account);
            }
        }
    }
}
