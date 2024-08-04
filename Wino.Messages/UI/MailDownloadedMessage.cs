using Wino.Core.Domain.Entities;

namespace Wino.Messaging.UI
{
    public record MailDownloadedMessage(MailCopy DownloadedMail) : UIMessageBase<MailDownloadedMessage>;
}
