using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Messaging.UI;

public record MailRemovedMessage(MailCopy RemovedMail, EntityUpdateSource Source = EntityUpdateSource.Server) : UIMessageBase<MailRemovedMessage>;
