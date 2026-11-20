using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Messaging.UI;

public record AccountRemovedMessage(MailAccount Account) : UIMessageBase<AccountRemovedMessage>;
