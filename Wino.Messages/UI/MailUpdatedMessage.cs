using Wino.Core.Domain.Entities.Mail;

namespace Wino.Messaging.UI;

public record MailUpdatedMessage(MailCopy UpdatedMail) : UIMessageBase<MailUpdatedMessage>;
