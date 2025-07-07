using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Domain.Interfaces;

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
    Task CreateAccountAsync(MailAccount account, CustomServerInformation customServerInformation);

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

    /// <summary>
    /// Returns the account aliases.
    /// </summary>
    /// <param name="accountId">Account id.</param>
    /// <returns>A list of MailAccountAlias that has e-mail aliases.</returns>
    Task<List<MailAccountAlias>> GetAccountAliasesAsync(Guid accountId);

    /// <summary>
    /// Updated account's aliases.
    /// </summary>
    /// <param name="accountId">Account id to update aliases for.</param>
    /// <param name="aliases">Full list of updated aliases.</param>
    /// <returns></returns>
    Task UpdateAccountAliasesAsync(Guid accountId, List<MailAccountAlias> aliases);

    /// <summary>
    /// Delete account alias.
    /// </summary>
    /// <param name="aliasId">Alias to remove.</param>
    Task DeleteAccountAliasAsync(Guid aliasId);

    /// <summary>
    /// Updated profile information of the account.
    /// </summary>
    /// <param name="accountId">Account id to update info for.</param>
    /// <param name="profileInformation">Info data.</param>
    /// <returns></returns>
    Task UpdateProfileInformationAsync(Guid accountId, ProfileInformation profileInformation);


    /// <summary>
    /// Creates a root + primary alias for the account.
    /// This is only called when the account is created.
    /// </summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="address">Address to create root primary alias from.</param>
    Task CreateRootAliasAsync(Guid accountId, string address);

    /// <summary>
    /// Will compare local-remote aliases and update the local ones or add/delete new ones.
    /// </summary>
    /// <param name="remoteAccountAliases">Remotely fetched basic alias info from synchronizer.</param>
    /// <param name="account">Account to update remote aliases for..</param>
    Task UpdateRemoteAliasInformationAsync(MailAccount account, List<RemoteAccountAlias> remoteAccountAliases);

    /// <summary>
    /// Gets the primary account alias for the given account id.
    /// Used when creating draft messages.
    /// </summary>
    /// <param name="accountId">Account id.</param>
    /// <returns>Primary alias for the account.</returns>
    Task<MailAccountAlias> GetPrimaryAccountAliasAsync(Guid accountId);
    Task<bool> IsAccountFocusedEnabledAsync(Guid accountId);

    /// <summary>
    /// Deletes mail cache in the database for the given account.
    /// </summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="AccountCacheResetReason">Reason for the cache reset.</param>
    Task DeleteAccountMailCacheAsync(Guid accountId, AccountCacheResetReason accountCacheResetReason);

    /// <summary>
    /// Updates the synchronization identifier for a specific account asynchronously.
    /// </summary>
    /// <param name="accountId">Identifies the account for which the synchronization identifier is being updated.</param>
    /// <param name="syncIdentifier">Represents the new synchronization identifier to be set for the specified account.</param>
    Task<string> UpdateSyncIdentifierRawAsync(Guid accountId, string syncIdentifier);


    /// <summary>
    /// Gets whether the notifications are enabled for the given account id.
    /// </summary>
    /// <param name="accountId">Account id.</param>
    /// <returns>Whether the notifications should be created after sync or not.</returns>
    Task<bool> IsNotificationsEnabled(Guid accountId);
    Task UpdateAccountCustomServerInformationAsync(CustomServerInformation customServerInformation);
}
