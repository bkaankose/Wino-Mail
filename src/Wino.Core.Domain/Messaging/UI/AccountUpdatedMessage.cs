using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Messaging.UI;

public record AccountUpdatedMessage(MailAccount Account) : UIMessageBase<AccountUpdatedMessage>;
