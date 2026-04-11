using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Mail;

public class SentMailReceiptState
{
    [PrimaryKey]
    public Guid MailUniqueId { get; set; }

    public Guid AccountId { get; set; }

    public string MessageId { get; set; }

    public bool IsReceiptRequested { get; set; }

    public DateTime RequestedAtUtc { get; set; }

    public SentMailReceiptStatus Status { get; set; }

    public DateTime? AcknowledgedAtUtc { get; set; }

    public Guid? ReceiptMessageUniqueId { get; set; }
}
