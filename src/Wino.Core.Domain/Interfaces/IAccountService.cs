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
    /// Checks whether an account with the same display name already exists.
    /// </summary>
    /// <param name="name">Account display name.</param>
    /// <param name="excludedAccountId">Optional account id to exclude from the check.</param>
    Task<bool> AccountNameExistsAsync(string name, Guid? excludedAccountId = null);

    /// <summary>
    /// Checks whether an account with the same primary address already exists.
    /// </summary>
    /// <param name="address">Primary e-mail address.</param>
    /// <param name="excludedAccountId">Optional account id to exclude from the check.</param>
    Task<bool> AccountAddressExistsAsync(string address, Guid? excludedAccountId = null);

    /// <summary>
    /// Returns single MailAccount
    /// </summary>
    /// <param name="accountId">AccountId.</param>
    Task<MailAccount> GetAccountAsync(Guid accountId);
    /// <summary>Detached account/preferences/server endpoint metadata for explicit recovery.
    /// Every credential or opaque credential reference is removed; this is never a hydrated account.
    /// Does not access credential storage and must not be used to authenticate.</summary>
    Task<MailAccount> GetAccountMetadataAsync(Guid accountId);

    /// <summary>
    /// Deletes all information about the account, including token information.
    /// </summary>
    /// <param name="account">MailAccount to be removed</param>
    Task DeleteAccountAsync(MailAccount account);

    /// <summary>
    /// Removes the credentials for an account without deleting its locally synchronized data.
    /// The account is marked as requiring authentication before it can synchronize again.
    /// </summary>
    /// <param name="accountId">Account whose authentication data will be removed.</param>
    Task DeleteAccountAuthenticationDataAsync(Guid accountId);

    /// <summary>
    /// Returns the custom server information for the given account id.
    /// </summary>
    Task<CustomServerInformation> GetAccountCustomServerInformationAsync(Guid accountId);

    /// <summary>
    /// Updates the given account properties.
    /// </summary>
    Task UpdateAccountAsync(MailAccount account);

    /// <summary>Loads the local preferences for one mailbox.</summary>
    Task<MailAccountPreferences> GetAccountPreferencesAsync(Guid accountId);

    /// <summary>
    /// Persists local mailbox preferences and notifies loaded presentation surfaces.
    /// These preferences never participate in profile export or cloud synchronization.
    /// </summary>
    Task UpdateAccountPreferencesAsync(MailAccountPreferences preferences);

    /// <summary>
    /// Creates new account with the given server information if any.
    /// Also sets the account as Startup account if there are no accounts.
    /// </summary>
    Task CreateAccountAsync(
        MailAccount account,
        CustomServerInformation? customServerInformation,
        bool shouldAppendMessagesToSentFolder = true,
        bool enableMailFilters = false);

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
    /// Creates an account group from the given accounts. A group needs at least two accounts; fewer are refused.
    /// </summary>
    /// <param name="mergedInbox">Group properties. Its id is replaced.</param>
    /// <param name="accountsToMerge">Accounts of the group.</param>
    Task CreateMergeAccountsAsync(MergedInbox mergedInbox, IEnumerable<MailAccount> accountsToMerge);

    /// <summary>
    /// Replaces the accounts of the group with the given id. A group needs at least two accounts; fewer are refused.
    /// </summary>
    /// <param name="mergedInboxId">Updating group id.</param>
    /// <param name="linkedAccountIds">Account ids of the group.</param>
    Task UpdateMergedInboxAsync(Guid mergedInboxId, IEnumerable<Guid> linkedAccountIds);

    /// <summary>
    /// Deletes the group with the given id. Its accounts stay and return to the top level.
    /// </summary>
    /// <param name="mergedInboxId">Group id to delete.</param>
    Task UnlinkMergedInboxAsync(Guid mergedInboxId);

    /// <summary>
    /// Updates the account listing orders. Keys are account or group ids; groups keep their accounts together.
    /// </summary>
    /// <param name="accountIdOrderPair">AccountId-OrderNumber pair for all accounts.</param>
    Task UpdateAccountOrdersAsync(Dictionary<Guid, int> accountIdOrderPair);

    /// <summary>Returns every account group.</summary>
    Task<List<MergedInbox>> GetAccountGroupsAsync();

    /// <summary>
    /// Creates the group, or saves an existing one: its name, color, icon, folder combination and ordered accounts.
    /// Accounts that are in another group move to this one. Throws when fewer than two accounts are given.
    /// </summary>
    /// <returns>The saved group.</returns>
    Task<MergedInbox> SaveAccountGroupAsync(MergedInbox group, IReadOnlyList<Guid> memberAccountIds);

    /// <summary>Persists whether a group is expanded in the navigation panes, without rebuilding them.</summary>
    Task SetAccountGroupExpandedAsync(Guid groupId, bool isExpanded);

    /// <summary>
    /// Saves the order of the top-level entries (groups and accounts) and of the accounts inside each group.
    /// Membership never changes here.
    /// </summary>
    Task SaveAccountLayoutOrderAsync(IEnumerable<AccountLayoutOrderEntry> topLevelEntries);

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
    /// Adds one alias to an account, normalizing its address first.
    /// Existing rows are left exactly as they are, including legacy duplicates of the same
    /// address, because consolidating them here would silently change what the user sees.
    /// </summary>
    /// <returns>False when the account already has that address; the alias is not inserted.</returns>
    Task<bool> AddAccountAliasAsync(Guid accountId, MailAccountAlias alias);

    /// <summary>
    /// Makes one alias the account default. Only the primary flag changes, so a refresh that
    /// disagrees with the user cannot undo the choice.
    /// </summary>
    /// <exception cref="InvalidOperationException">The alias does not belong to that account.</exception>
    Task SetDefaultAccountAliasAsync(Guid accountId, Guid aliasId);

    /// <summary>Sets, or with a null thumbprint clears, the signing certificate of one alias.</summary>
    /// <exception cref="InvalidOperationException">The alias does not belong to that account.</exception>
    Task SetAliasSigningCertificateAsync(Guid accountId, Guid aliasId, string thumbprint);

    /// <summary>Turns S/MIME encryption on or off for one alias.</summary>
    /// <exception cref="InvalidOperationException">The alias does not belong to that account.</exception>
    Task SetAliasEncryptionAsync(Guid accountId, Guid aliasId, bool isEnabled);

    /// <summary>
    /// Updated profile information of the account.
    /// </summary>
    /// <param name="accountId">Account id to update info for.</param>
    /// <param name="profileInformation">Info data.</param>
    /// <returns></returns>
    Task UpdateProfileInformationAsync(
        Guid accountId,
        ProfileInformation profileInformation,
        bool removePictureWhenConfirmedAbsent = false);


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

    /// <summary>
    /// Returns the primary alias of the account. Repairs an account that has no alias rows
    /// (restored or migrated databases) by creating the root primary alias from the account address.
    /// </summary>
    /// <param name="accountId">Account id.</param>
    /// <returns>Primary alias, or null when the account does not exist or has no address.</returns>
    Task<MailAccountAlias> EnsurePrimaryAccountAliasAsync(Guid accountId);
    Task UpdateAliasSendCapabilityAsync(Guid accountId, string aliasAddress, AliasSendCapability capability);
    Task<bool> IsAccountFocusedEnabledAsync(Guid accountId);

    /// <summary>
    /// Deletes mail cache in the database for the given account.
    /// </summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="AccountCacheResetReason">Reason for the cache reset.</param>
    Task DeleteAccountMailCacheAsync(Guid accountId, AccountCacheResetReason accountCacheResetReason);

    /// <summary>
    /// Removes the account's downloaded messages and MIME files and clears its delta identifiers,
    /// so the next mail synchronization starts from scratch. Folders and their settings are kept.
    /// Used when the mail mode is turned off for an account that keeps existing.
    /// </summary>
    Task DeleteAccountMailDataAsync(Guid accountId);

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
    Task UpdateImapConnectionSettingsAsync(MailAccount account, CustomServerInformation customServerInformation);

    /// <summary>
    /// Updates the last folder structure sync date for the given account.
    /// Used for optimization to skip folder sync if it was done recently.
    /// </summary>
    /// <param name="accountId">Account id.</param>
    Task UpdateLastFolderStructureSyncDateAsync(Guid accountId);

    /// <summary>
    /// Checks if folder structure should be synced based on the configured interval.
    /// Returns true if LastFolderStructureSyncDate is null or older than the interval.
    /// </summary>
    /// <param name="accountId">Account id.</param>
    /// <param name="syncInterval">Minimum interval between folder syncs.</param>
    Task<bool> ShouldSyncFolderStructureAsync(Guid accountId, TimeSpan syncInterval);
}
