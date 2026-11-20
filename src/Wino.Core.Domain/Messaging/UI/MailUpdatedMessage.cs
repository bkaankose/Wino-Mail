using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Messaging.UI;

public record MailUpdatedMessage(MailCopy UpdatedMail, EntityUpdateSource Source, MailCopyChangeFlags ChangedProperties = MailCopyChangeFlags.None) : UIMessageBase<MailUpdatedMessage>;
