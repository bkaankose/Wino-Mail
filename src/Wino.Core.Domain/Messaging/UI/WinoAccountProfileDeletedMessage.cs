using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Messaging.UI;

public record WinoAccountProfileDeletedMessage(WinoAccount Account) : UIMessageBase<WinoAccountProfileDeletedMessage>;
