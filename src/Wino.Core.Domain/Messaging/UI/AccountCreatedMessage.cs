using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Messaging.UI;

public record AccountCreatedMessage(MailAccount Account) : UIMessageBase<AccountCreatedMessage>;
