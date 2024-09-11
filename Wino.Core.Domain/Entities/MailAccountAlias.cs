using System;
using SQLite;

namespace Wino.Core.Domain.Entities
{
    public class RemoteAccountAlias
    {
        /// <summary>
        /// Display address of the alias.
        /// </summary>
        public string AliasAddress { get; set; }

        /// <summary>
        /// Address to be included in Reply-To header when alias is used for sending messages.
        /// </summary>
        public string ReplyToAddress { get; set; }

        /// <summary>
        /// Whether this alias is the primary alias for the account.
        /// </summary>
        public bool IsPrimary { get; set; }

        /// <summary>
        /// Whether the alias is verified by the server.
        /// Only Gmail aliases are verified for now.
        /// Non-verified alias messages might be rejected by SMTP server.
        /// </summary>
        public bool IsVerified { get; set; }

        /// <summary>
        /// Whether this alias is the root alias for the account.
        /// Root alias means the first alias that was created for the account.
        /// It can't be deleted or changed.
        /// </summary>
        public bool IsRootAlias { get; set; }

        /// <summary>
        /// Optional sender name for the alias.
        /// Falls back to account's sender name if not set when preparing messages.
        /// Used for Gmail only.
        /// </summary>
        public string AliasSenderName { get; set; }
    }

    public class MailAccountAlias : RemoteAccountAlias
    {
        /// <summary>
        /// Unique Id for the alias.
        /// </summary>
        [PrimaryKey]
        public Guid Id { get; set; }

        /// <summary>
        /// Account id that this alias is attached to.
        /// </summary>
        public Guid AccountId { get; set; }

        /// <summary>
        /// Root aliases can't be deleted.
        /// </summary>
        public bool CanDelete => !IsRootAlias;
    }
}
