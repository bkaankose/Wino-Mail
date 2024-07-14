using System;
using Wino.Core.Domain.Entities;

namespace Wino.Core.Domain.Models.MailItem
{
    /// <summary>
    /// Interface of simplest representation of a MailCopy.
    /// </summary>
    public interface IMailItem : IMailHashContainer
    {
        Guid UniqueId { get; }
        string Id { get; }
        string Subject { get; }
        string ThreadId { get; }
        string MessageId { get; }
        string References { get; }
        string InReplyTo { get; }
        string PreviewText { get; }
        string FromName { get; }
        DateTime CreationDate { get; }
        string FromAddress { get; }
        bool HasAttachments { get; }
        bool IsFlagged { get; }
        bool IsFocused { get; }
        bool IsRead { get; }
        string DraftId { get; }
        bool IsDraft { get; }
        Guid FileId { get; }

        MailItemFolder AssignedFolder { get; }
        MailAccount AssignedAccount { get; }
    }
}
