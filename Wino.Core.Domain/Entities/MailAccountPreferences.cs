using System;
using SQLite;

namespace Wino.Core.Domain.Entities
{
    public class MailAccountPreferences
    {
        [PrimaryKey]
        public Guid Id { get; set; }

        /// <summary>
        /// Id of the account in MailAccount table.
        /// </summary>
        public Guid AccountId { get; set; }

        /// <summary>
        /// Gets or sets whether sent draft messages should be appended to the sent folder.
        /// Some IMAP servers do this automatically, some don't.
        /// It's disabled by default.
        /// </summary>
        public bool ShouldAppendMessagesToSentFolder { get; set; }

        /// <summary>
        /// Gets or sets whether the notifications are enabled for the account.
        /// </summary>
        public bool IsNotificationsEnabled { get; set; }

        /// <summary>
        /// Gets or sets whether the account has Focused inbox support.
        /// Null if the account provider type doesn't support Focused inbox.
        /// </summary>
        public bool? IsFocusedInboxEnabled { get; set; }
    }
}
