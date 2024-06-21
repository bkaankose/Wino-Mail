﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Domain.Interfaces
{
    public interface IAccountService
    {
        /// <summary>
        /// Current IAuthenticator that should receive external authentication process to continue with.
        /// For example: Google auth will launch a browser authentication. After it completes, this is the IAuthenticator
        /// to continue process for token exchange.
        /// </summary>
        IAuthenticator ExternalAuthenticationAuthenticator { get; set; }

        /// <summary>
        /// Returns all local accounts.
        /// </summary>
        /// <returns>All local accounts</returns>
        Task<List<MailAccount>> GetAccountsAsync();

        /// <summary>
        /// Returns single MailAccount
        /// </summary>
        /// <param name="accountId">AccountId.</param>
        Task<MailAccount> GetAccountAsync(Guid accountId);

        /// <summary>
        /// Deletes all information about the account, including token information.
        /// </summary>
        /// <param name="account">MailAccount to be removed</param>
        Task DeleteAccountAsync(MailAccount account);

        /// <summary>
        /// Returns the custom server information for the given account id.
        /// </summary>
        Task<CustomServerInformation> GetAccountCustomServerInformationAsync(Guid accountId);

        /// <summary>
        /// Updates the given account properties.
        /// </summary>
        Task UpdateAccountAsync(MailAccount account);

        /// <summary>
        /// Creates new account with the given server information if any.
        /// Also sets the account as Startup account if there are no accounts.
        /// </summary>
        Task CreateAccountAsync(MailAccount account, TokenInformation tokenInformation, CustomServerInformation customServerInformation);

        /// <summary>
        /// Fixed authentication errors for account by forcing interactive login.
        /// </summary>
        Task FixTokenIssuesAsync(Guid accountId);

        /// <summary>
        /// Removed the attention from an account.
        /// </summary>
        /// <param name="accountId">Account id to remove from</param>
        Task ClearAccountAttentionAsync(Guid accountId);

        /// <summary>
        /// Updates the account synchronization identifier.
        /// For example: Gmail uses this identifier to keep track of the last synchronization.
        /// Update is ignored for Gmail if the new identifier is older than the current one.
        /// </summary>
        /// <param name="newIdentifier">Identifier to update</param>
        /// <returns>Current account synchronization modifier.</returns>
        Task<string> UpdateSynchronizationIdentifierAsync(Guid accountId, string newIdentifier);

        /// <summary>
        /// Renames the merged inbox with the given id.
        /// </summary>
        /// <param name="mergedInboxId">Merged Inbox id</param>
        /// <param name="newName">New name for the merged/linked inbox.</param>
        Task RenameMergedAccountAsync(Guid mergedInboxId, string newName);

        /// <summary>
        /// Creates a new merged inbox with the given accounts.
        /// </summary>
        /// <param name="mergedInbox">Merged inbox properties.</param>
        /// <param name="accountsToMerge">List of accounts to merge together.</param>
        Task CreateMergeAccountsAsync(MergedInbox mergedInbox, IEnumerable<MailAccount> accountsToMerge);

        /// <summary>
        /// Updates the merged inbox with the given id with the new linked accounts.
        /// </summary>
        /// <param name="mergedInboxId">Updating merged inbox id.</param>
        /// <param name="linkedAccountIds">List of linked account ids.</param>
        Task UpdateMergedInboxAsync(Guid mergedInboxId, IEnumerable<Guid> linkedAccountIds);

        /// <summary>
        /// Destroys the merged inbox with the given id.
        /// </summary>
        /// <param name="mergedInboxId">Merged inbox id to destroy.</param>
        Task UnlinkMergedInboxAsync(Guid mergedInboxId);

        /// <summary>
        /// Updates the account listing orders.
        /// </summary>
        /// <param name="accountIdOrderPair">AccountId-OrderNumber pair for all accounts.</param>
        Task UpdateAccountOrdersAsync(Dictionary<Guid, int> accountIdOrderPair);
    }
}
