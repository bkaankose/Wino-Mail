using Wino.Core.Domain.Entities.Shared;

namespace Wino.Core.Domain.Messaging.UI;

public record WinoAccountSignedOutMessage(WinoAccount Account) : UIMessageBase<WinoAccountSignedOutMessage>;
