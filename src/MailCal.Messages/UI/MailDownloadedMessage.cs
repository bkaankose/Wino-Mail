using MailCal.Core.Domain.Entities.Mail;

namespace MailCal.Messaging.UI;

public record MailDownloadedMessage(MailCopy DownloadedMail) : UIMessageBase<MailDownloadedMessage>;
