using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Messaging.UI;

public record MailAddedMessage(MailCopy AddedMail, EntityUpdateSource Source = EntityUpdateSource.Server) : UIMessageBase<MailAddedMessage>;
