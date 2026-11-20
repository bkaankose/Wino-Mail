using Wino.Core.Domain.Entities.Mail;

namespace Wino.Core.Domain.Messaging.UI;

public record MailDownloadedMessage(MailCopy DownloadedMail) : UIMessageBase<MailDownloadedMessage>;
