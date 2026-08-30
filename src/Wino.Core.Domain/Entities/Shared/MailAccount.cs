using System;
using SQLite;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared;

public class MailAccount
{
    [PrimaryKey]
    public Guid Id { get; set; }

    /// <summary>
    /// Given name of the account in Wino.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// TODO: Display name of the authenticated user/account.
    /// API integrations will query this value from the API.
    /// IMAP is populated by user on setup dialog.
    /// </summary>

    public string SenderName { get; set; }

    /// <summary>
    /// Account e-mail address.
    /// </summary>
    public string Address { get; set; }

    /// <summary>
    /// Provider account address used for token cache lookup.
    /// This can differ from the mailbox address for Microsoft accounts that sign in with an alias.
    /// </summary>
    public string AuthenticationAddress { get; set; }

    /// <summary>
    /// Provider type of the account. Outlook,Gmail etc...
    /// </summary>
    public MailProviderType ProviderType { get; set; }

    /// <summary>
    /// For tracking mail change delta.
    /// Gmail  : historyId
    /// Outlook: deltaToken
    /// </summary>
    public string SynchronizationDeltaIdentifier { get; set; }

    /// <summary>
    /// For tracking calendar change delta.
    /// Gmail: It's per-calendar, so unused.
    /// Outlook: deltaLink
    /// </summary>
    public string CalendarSynchronizationDeltaIdentifier { get; set; }

    /// <summary>
    /// TODO: Gets or sets the custom account identifier color in hex.
    /// </summary>
    public string AccountColorHex { get; set; }

    /// <summary>
    /// Base64 encoded profile picture of the account.
    /// </summary>
    public string Base64ProfilePictureData { get; set; }

    /// <summary>
    /// Identifier of the normalized account-owned profile picture in app-local storage.
    /// </summary>
    public Guid? ProfilePictureFileId { get; set; }

    /// <summary>
    /// Whether the one-time provider profile-picture backfill has resolved this account.
    /// </summary>
    public bool IsProfilePictureBackfillComplete { get; set; }

    /// <summary>
    /// Gets or sets the listing order of the account in the accounts list.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// Gets or sets whether the account has any reason for an interactive user action to fix continue operating.
    /// </summary>
    public AccountAttentionReason AttentionReason { get; set; }

    /// <summary>
    /// Gets or sets the id of the merged inbox this account belongs to.
    /// </summary>
    public Guid? MergedInboxId { get; set; }

    /// <summary>
    /// Gets or sets the additional IMAP provider assignment for the account.
    /// Providers that use IMAP as a synchronizer but have special requirements.
    /// </summary>
    public SpecialImapProvider SpecialImapProvider { get; set; }

    /// <summary>
    /// Gets or sets whether mail access is granted for this account.
    /// When false, mail folders, aliases, compose flows, and mail synchronization are unavailable.
    /// Default is true for legacy accounts to preserve existing behavior.
    /// </summary>
    public bool IsMailAccessGranted { get; set; } = true;

    /// <summary>
    /// Gets or sets whether calendar access is granted for this account.
    /// When false, synchronizers will not process EventMessages or calendar invitations.
    /// Default is false for existing accounts to prevent scope issues.
    /// New accounts created after this feature will have this set to true.
    /// </summary>
    public bool IsCalendarAccessGranted { get; set; }

    /// <summary>Gets or sets whether the calendar mode is enabled, independently of remote authorization.</summary>
    public bool IsCalendarAccessEnabled { get; set; }

    /// <summary>Selects the calendar backend independently from the mail provider.</summary>
    public AccountIntegrationSource CalendarIntegrationSource { get; set; } = AccountIntegrationSource.Local;

    public bool IsContactAccessGranted { get; set; }

    /// <summary>Gets or sets whether the contacts mode is enabled, independently of remote authorization.</summary>
    public bool IsContactAccessEnabled { get; set; } = true;

    public bool IsContactReauthorizationRequired { get; set; }

    /// <summary>
    /// Selects the contact backend independently from the account's mail provider.
    /// Existing accounts are migrated explicitly so IMAP accounts remain local-only.
    /// </summary>
    public AccountIntegrationSource ContactIntegrationSource { get; set; } = AccountIntegrationSource.Local;

    /// <summary>
    /// Gets or sets whether provider task access is enabled for this account.
    /// Existing accounts default to false so migration never requests a new scope.
    /// IMAP-family accounts use their local task list regardless of this flag.
    /// </summary>
    public bool IsTaskAccessGranted { get; set; }

    /// <summary>Gets or sets whether the To Do mode is enabled, independently of remote authorization.</summary>
    public bool IsTaskAccessEnabled { get; set; }

    /// <summary>Selects the task backend independently from the mail provider.</summary>
    public AccountIntegrationSource TaskIntegrationSource { get; set; } = AccountIntegrationSource.Local;

    /// <summary>Gets or sets whether task authorization must be renewed.</summary>
    public bool IsTaskReauthorizationRequired { get; set; }

    /// <summary>
    /// Gets or sets whether MailKit incoming-mail and SMTP protocol diagnostics are captured for this account.
    /// Protocol logging is opt-in and disabled by default.
    /// </summary>
    public bool IsProtocolLogEnabled { get; set; }

    /// <summary>
    /// Contains the merged inbox this account belongs to.
    /// Ignored for all SQLite operations.
    /// </summary>
    [Ignore]
    public MergedInbox MergedInbox { get; set; }

    /// <summary>
    /// Populated only when account has custom server information.
    /// </summary>

    [Ignore]
    public CustomServerInformation ServerInformation { get; set; }

    /// <summary>
    /// Account preferences.
    /// </summary>
    [Ignore]
    public MailAccountPreferences Preferences { get; set; }

    /// <summary>
    /// Last time folder structure was synchronized.
    /// Used for optimization - skip folder sync if synced recently.
    /// </summary>
    public DateTime? LastFolderStructureSyncDate { get; set; }

    /// <summary>
    /// Gets or sets when the account was created in Wino.
    /// </summary>
    public DateTime? CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the timespan used for the account's initial mail synchronization.
    /// </summary>
    public InitialSynchronizationRange InitialSynchronizationRange { get; set; } = InitialSynchronizationRange.SixMonths;

    /// <summary>
    /// Gets whether the account can perform ProfileInformation sync type.
    /// </summary>
    public bool IsProfileInfoSyncSupported => ProviderType == MailProviderType.Outlook || ProviderType == MailProviderType.Gmail;

    /// <summary>
    /// Gets whether the account can perform AliasInformation sync type.
    /// </summary>
    public bool IsAliasSyncSupported => ProviderType == MailProviderType.Gmail || ProviderType == MailProviderType.Outlook;

    /// <summary>
    /// Gets whether the account can perform category definition sync type.
    /// </summary>
    public bool IsCategorySyncSupported => ProviderType == MailProviderType.Outlook;

    public override string ToString() => Name;
}
