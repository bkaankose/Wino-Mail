using Wino.Core.Domain.Entities.Shared;

namespace Wino.Messaging.UI;

public record WinoAccountProfileUpdatedMessage(WinoAccount Account) : UIMessageBase<WinoAccountProfileUpdatedMessage>;
