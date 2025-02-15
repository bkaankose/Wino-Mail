using Wino.Core.Domain.Entities.Mail;

namespace Wino.Messaging.UI;

public record MailRemovedMessage(MailCopy RemovedMail) : UIMessageBase<MailRemovedMessage>;
