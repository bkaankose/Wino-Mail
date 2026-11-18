using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Messaging.UI;

public record WinoAccountSignedInMessage(WinoAccount Account) : UIMessageBase<WinoAccountSignedInMessage>;
