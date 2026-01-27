using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.UI;

public record MailUpdatedMessage(MailCopy UpdatedMail, MailUpdateSource Source) : UIMessageBase<MailUpdatedMessage>;
