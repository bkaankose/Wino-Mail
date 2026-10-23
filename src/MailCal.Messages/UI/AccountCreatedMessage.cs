using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Messaging.UI;

public record AccountCreatedMessage(MailAccount Account) : UIMessageBase<AccountCreatedMessage>;
