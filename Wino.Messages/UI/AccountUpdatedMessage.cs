using Wino.Core.Domain.Entities.Shared;

namespace Wino.Messaging.UI;

public record AccountUpdatedMessage(MailAccount Account) : UIMessageBase<AccountUpdatedMessage>;
