using Wino.Core.Domain.Entities.Mail;

namespace Wino.Messaging.UI
{
    public record MailDownloadedMessage(MailCopy DownloadedMail) : UIMessageBase<MailDownloadedMessage>;
}
