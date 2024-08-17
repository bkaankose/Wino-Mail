using System;
using System.Collections.Generic;
using SQLite;

namespace Wino.Core.Domain.Entities
{
    public class MailAccountAlias
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
        /// Whether this alias is the root alias for the account.
        /// Root alias means the first alias that was created for the account.
        /// It can't be deleted or changed.
        /// </summary>
        public bool IsRootAlias { get; set; }

        /// <summary>
        /// Whether the alias is verified by the server.
        /// Non-verified aliases will show an info tip to users during sending.
        /// Only Gmail aliases are verified for now.
        /// Non-verified alias messages might be rejected by SMTP server.
        /// </summary>
        public bool IsVerified { get; set; }

        /// <summary>
        /// Root aliases can't be deleted.
        /// </summary>
        public bool CanDelete => !IsRootAlias;

        public override bool Equals(object obj)
        {
            if (obj == null || GetType() != obj.GetType())
                return false;

            var other = (MailAccountAlias)obj;
            return other != null &&
                AccountId == other.AccountId &&
                AliasAddress == other.AliasAddress &&
                ReplyToAddress == other.ReplyToAddress &&
                IsPrimary == other.IsPrimary &&
                IsVerified == other.IsVerified &&
                IsRootAlias == other.IsRootAlias;
        }

        public override int GetHashCode()
        {
            int hashCode = 59052167;
            hashCode = hashCode * -1521134295 + AccountId.GetHashCode();
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(AliasAddress);
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(ReplyToAddress);
            hashCode = hashCode * -1521134295 + IsPrimary.GetHashCode();
            hashCode = hashCode * -1521134295 + IsRootAlias.GetHashCode();
            hashCode = hashCode * -1521134295 + IsVerified.GetHashCode();
            hashCode = hashCode * -1521134295 + CanDelete.GetHashCode();
            return hashCode;
        }
    }
}
