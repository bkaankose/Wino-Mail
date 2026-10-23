using System.Collections.Generic;
using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;

namespace MailCal.Messaging.UI;

public record BulkMailUpdatedMessage(
    IReadOnlyList<MailCopy> UpdatedMails,
    EntityUpdateSource Source = EntityUpdateSource.Server,
    MailCopyChangeFlags ChangedProperties = MailCopyChangeFlags.None) : UIMessageBase<BulkMailUpdatedMessage>;
