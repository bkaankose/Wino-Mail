using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;

namespace MailCal.Messaging.UI;

public record MailRemovedMessage(MailCopy RemovedMail, EntityUpdateSource Source = EntityUpdateSource.Server) : UIMessageBase<MailRemovedMessage>;
