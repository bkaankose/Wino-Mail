using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Messaging.UI;

public record WinoAccountProfileUpdatedMessage(WinoAccount Account) : UIMessageBase<WinoAccountProfileUpdatedMessage>;
