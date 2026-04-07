using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Messaging.UI;

public record MailAddedMessage(MailCopy AddedMail, EntityUpdateSource Source = EntityUpdateSource.Server) : UIMessageBase<MailAddedMessage>;
