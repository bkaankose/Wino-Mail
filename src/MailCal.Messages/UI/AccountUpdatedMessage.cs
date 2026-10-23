using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Messaging.UI;

public record AccountUpdatedMessage(MailAccount Account) : UIMessageBase<AccountUpdatedMessage>;
