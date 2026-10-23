using MailCal.Core.Domain.Entities.Shared;

namespace MailCal.Messaging.UI;

public record AccountRemovedMessage(MailAccount Account) : UIMessageBase<AccountRemovedMessage>;
