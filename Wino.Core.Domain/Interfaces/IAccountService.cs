using System;
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

        Task RenameMergedAccountAsync(Guid mergedInboxId, string newName);

        Task CreateMergeAccountsAsync(MergedInbox mergedInbox, IEnumerable<MailAccount> accountsToMerge);

        Task UpdateMergedInboxAsync(Guid mergedInboxId, IEnumerable<Guid> linkedAccountIds);

        Task UnlinkMergedInboxAsync(Guid mergedInboxId);
    }
}
