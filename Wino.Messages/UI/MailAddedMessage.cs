using Wino.Core.Domain.Entities.Mail;

namespace Wino.Messaging.UI;

public record MailAddedMessage(MailCopy AddedMail) : UIMessageBase<MailAddedMessage>;
