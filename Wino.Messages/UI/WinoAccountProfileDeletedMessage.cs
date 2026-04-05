using Wino.Core.Domain.Entities.Shared;

namespace Wino.Messaging.UI;

public record WinoAccountProfileDeletedMessage(WinoAccount Account) : UIMessageBase<WinoAccountProfileDeletedMessage>;
