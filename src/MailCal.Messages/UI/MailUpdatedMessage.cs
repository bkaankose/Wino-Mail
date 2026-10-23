using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;

namespace MailCal.Messaging.UI;

public record MailUpdatedMessage(MailCopy UpdatedMail, EntityUpdateSource Source, MailCopyChangeFlags ChangedProperties = MailCopyChangeFlags.None) : UIMessageBase<MailUpdatedMessage>;
