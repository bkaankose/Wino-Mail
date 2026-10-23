using MailCal.Core.Domain.Entities.Mail;
using MailCal.Core.Domain.Enums;

namespace MailCal.Messaging.UI;

public record MailAddedMessage(MailCopy AddedMail, EntityUpdateSource Source = EntityUpdateSource.Server) : UIMessageBase<MailAddedMessage>;
