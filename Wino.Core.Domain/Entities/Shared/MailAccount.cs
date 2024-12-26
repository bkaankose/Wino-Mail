using System;
using SQLite;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Shared
{
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
        /// TODO: Gets or sets the custom account identifier color in hex.
        /// </summary>
        public string AccountColorHex { get; set; }

        /// <summary>
        /// Base64 encoded profile picture of the account.
        /// </summary>
        public string Base64ProfilePictureData { get; set; }

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
        /// Gets whether the account can perform ProfileInformation sync type.
        /// </summary>
        public bool IsProfileInfoSyncSupported => ProviderType == MailProviderType.Outlook || ProviderType == MailProviderType.Office365 || ProviderType == MailProviderType.Gmail;

        /// <summary>
        /// Gets whether the account can perform AliasInformation sync type.
        /// </summary>
        public bool IsAliasSyncSupported => ProviderType == MailProviderType.Gmail;
    }
}
