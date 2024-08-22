using System;
using System.Collections.Generic;
using SQLite;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;

namespace Wino.Core.Domain.Entities
{
    /// <summary>
    /// Summary of the parsed MIME messages.
    /// Wino will do non-network operations on this table and others from the original MIME.
    /// </summary>
    public class MailCopy : IMailItem
    {
        /// <summary>
        /// Unique Id of the mail.
        /// </summary>
        [PrimaryKey]
        public Guid UniqueId { get; set; }

        /// <summary>
        /// Not unique id of the item. Some operations held on this Id, some on the UniqueId.
        /// Same message can be in different folder. In that case UniqueId is used.
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Folder that this mail belongs to.
        /// </summary>
        public Guid FolderId { get; set; }

        /// <summary>
        /// Conversation id for the mail.
        /// </summary>
        public string ThreadId { get; set; }

        /// <summary>
        /// MIME MessageId if exists.
        /// </summary>
        public string MessageId { get; set; }

        /// <summary>
        /// References header from MIME
        /// </summary>
        public string References { get; set; }

        /// <summary>
        /// In-Reply-To header from MIME
        /// </summary>
        public string InReplyTo { get; set; }

        /// <summary>
        /// Name for the sender.
        /// </summary>
        public string FromName { get; set; }

        /// <summary>
        /// Address of the sender.
        /// </summary>
        public string FromAddress { get; set; }

        /// <summary>
        /// Subject of the mail.
        /// </summary>
        public string Subject { get; set; }

        /// <summary>
        /// Short preview of the content.
        /// </summary>
        public string PreviewText { get; set; }

        /// <summary>
        /// Date that represents this mail has been created in provider servers.
        /// Stored always in UTC.
        /// </summary>
        public DateTime CreationDate { get; set; }

        /// <summary>
        /// Importance of the mail.
        /// </summary>
        public MailImportance Importance { get; set; }

        /// <summary>
        /// Read status for the mail.
        /// </summary>
        public bool IsRead { get; set; }

        /// <summary>
        /// Flag status.
        /// Flagged for Outlook.
        /// Important for Gmail.
        /// </summary>
        public bool IsFlagged { get; set; }

        /// <summary>
        /// To support Outlook.
        /// Gmail doesn't use it.
        /// </summary>
        public bool IsFocused { get; set; }

        /// <summary>
        /// Whether mail has attachments included or not.
        /// </summary>
        public bool HasAttachments { get; set; }

        /// <summary>
        /// Assigned draft id.
        /// </summary>
        public string DraftId { get; set; }

        /// <summary>
        /// Whether this mail is only created locally.
        /// </summary>
        [Ignore]
        public bool IsLocalDraft => !string.IsNullOrEmpty(DraftId) && DraftId.StartsWith(Constants.LocalDraftStartPrefix);

        /// <summary>
        /// Whether this copy is draft or not.
        /// </summary>
        public bool IsDraft { get; set; }

        /// <summary>
        /// File id that this mail is assigned to.
        /// This Id is immutable. It's used to find the file in the file system.
        /// Even after mapping local draft to remote draft, it will not change.
        /// </summary>
        public Guid FileId { get; set; }

        /// <summary>
        /// Folder that this mail is assigned to.
        /// Warning: This field is not populated by queries.
        /// Services or View Models are responsible for populating this field.
        /// </summary>
        [Ignore]
        public MailItemFolder AssignedFolder { get; set; }

        /// <summary>
        /// Account that this mail is assigned to.
        /// Warning: This field is not populated by queries.
        /// Services or View Models are responsible for populating this field.
        /// </summary>
        [Ignore]
        public MailAccount AssignedAccount { get; set; }

        /// <summary>
        /// Contact information of the sender if exists.
        /// Warning: This field is not populated by queries.
        /// Services or View Models are responsible for populating this field.
        /// </summary>
        [Ignore]
        public AccountContact SenderContact { get; set; }

        public IEnumerable<Guid> GetContainingIds() => [UniqueId];
        public override string ToString() => $"{Subject} <-> {Id}";
    }
}
